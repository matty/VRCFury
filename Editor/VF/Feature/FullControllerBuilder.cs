using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.ScriptableObjects;
using VF.Builder;
using VF.Feature.Base;
using VF.Inspector;
using VF.Model;
using VF.Model.Feature;
using VF.Model.StateAction;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Toggle = VF.Model.Feature.Toggle;

namespace VF.Feature {

    public class FullControllerBuilder : FeatureBuilder<FullController> {

        [FeatureBuilderAction]
        public void Apply() {
            var baseObject = model.rootObj;
            if (baseObject == null) {
                if (model.disableClipRewriting) {
                    baseObject = avatarObject;
                } else {
                    baseObject = featureBaseObject;
                }
            }

            var rewrittenParams = new HashSet<string>();
            Func<string,string> rewriteParam = name => {
                if (string.IsNullOrWhiteSpace(name)) return name;
                if (VRChatGlobalParams.Contains(name)) return name;
                if (model.allNonsyncedAreGlobal) {
                    var synced = model.prms.Any(p => p.parameters.parameters.Any(param => param.name == name));
                    if (!synced) return name;
                }
                if (model.globalParams.Contains(name)) {
                    return name;
                }

                rewrittenParams.Add(name);
                return ControllerManager.NewParamName("fc" + uniqueModelNum + "_" + name);
            };

            foreach (var p in model.prms) {
                foreach (var param in p.parameters.parameters) {
                    if (string.IsNullOrWhiteSpace(param.name)) continue;
                    var newParam = new VRCExpressionParameters.Parameter {
                        name = rewriteParam(param.name),
                        valueType = param.valueType,
                        saved = param.saved && !model.ignoreSaved,
                        defaultValue = param.defaultValue
                    };
                    manager.GetParams().addSyncedParam(newParam);
                }
            }

            var rewrittenClips = new Dictionary<AnimationClip, AnimationClip>();

            foreach (var c in model.controllers) {
                var type = c.type;
                var source = (AnimatorController)c.controller;
                
                AnimationClip RewriteClip(AnimationClip from) {
                    if (from == null) {
                        return null;
                    }
                    if (rewrittenClips.ContainsKey(from)) return rewrittenClips[from];
                    AnimationClip rewritten;
                    if (baseObject == avatarObject) {
                        rewritten = from;
                    } else {
                        rewritten = manager.GetClipStorage().NewClip(baseObject.name + "__" + from.name);
                        clipBuilder.CopyWithAdjustedPrefixes(from, rewritten, baseObject, model.removePrefixes);
                    }

                    rewrittenClips[from] = rewritten;
                    return rewritten;
                }
                
                BlendTree NewBlendTree(string name) {
                    return manager.GetClipStorage().NewBlendTree(baseObject.name + "__" + name);
                }

                var targetController = manager.GetController(type);
                if (type == VRCAvatarDescriptor.AnimLayerType.Gesture && source.layers.Length > 0 && targetController.GetRaw().layers.Length > 0) {
                    var sourceMask = AvatarManager.GetBaseMask(source);
                    var targetMask = AvatarManager.GetBaseMask(targetController.GetRaw());
                    if (targetMask == null) {
                        AvatarManager.SetBaseMask(targetController.GetRaw(), sourceMask);
                    } else if (sourceMask == null) {
                        // oh well?
                    } else {
                        var newPath = VRCFuryAssetDatabase.GetUniquePath(tmpDir, "gestureMask", "mask");
                        var copy = VRCFuryAssetDatabase.CopyAsset(targetMask, newPath);
                        foreach (var bodyPart in (AvatarMaskBodyPart[])Enum.GetValues(typeof(AvatarMaskBodyPart))) {
                            if (sourceMask.GetHumanoidBodyPartActive(bodyPart))
                                copy.SetHumanoidBodyPartActive(bodyPart, true);
                        }
                        for (var i = 0; i < sourceMask.transformCount; i++) {
                            if (sourceMask.GetTransformActive(i)) {
                                copy.transformCount++;
                                copy.SetTransformPath(targetMask.transformCount-1, sourceMask.GetTransformPath(i));
                                copy.SetTransformActive(targetMask.transformCount-1, true);
                            }
                        }
                        AvatarManager.SetBaseMask(targetController.GetRaw(), copy);
                    }
                }
                var merger = new ControllerMerger(
                    layerName => ControllerManager.NewLayerName("[FC" + uniqueModelNum + "] " + layerName),
                    param => rewriteParam(param),
                    RewriteClip,
                    NewBlendTree
                );
                merger.Merge(source, targetController.GetRaw());
            }

            foreach (var m in model.menus) {
                var prefix = string.IsNullOrWhiteSpace(m.prefix)
                    ? new string[] { }
                    : m.prefix.Split('/').ToArray();
                manager.GetMenu().MergeMenu(prefix, m.menu, rewriteParam);
            }
            
            foreach (var receiver in baseObject.GetComponentsInChildren<VRCContactReceiver>(true)) {
                if (rewrittenParams.Contains(receiver.parameter)) {
                    receiver.parameter = rewriteParam(receiver.parameter);
                }
            }
            foreach (var physbone in baseObject.GetComponentsInChildren<VRCPhysBone>(true)) {
                if (rewrittenParams.Contains(physbone.parameter + "_IsGrabbed") || rewrittenParams.Contains(physbone.parameter + "_Angle") || rewrittenParams.Contains(physbone.parameter + "_Stretch")) {
                    physbone.parameter = rewriteParam(physbone.parameter);
                }
            }

            if (model.toggleParam != null) {
                addOtherFeature(new Toggle {
                    name = rewriteParam(model.toggleParam),
                    state = new State {
                        actions = { new ObjectToggleAction { obj = baseObject } }
                    },
                    securityEnabled = true,
                    forceOffForUpload = true,
                    addMenuItem = false,
                    usePrefixOnParam = false
                });
            }
        }

        public override string GetEditorTitle() {
            return "Full Controller";
        }

        public override VisualElement CreateEditor(SerializedProperty prop) {
            var content = new VisualElement();
            content.Add(VRCFuryEditorUtils.WrappedLabel("Controllers:"));
            content.Add(VRCFuryEditorUtils.List(prop.FindPropertyRelative("controllers"),
                (i, el) => {
                    var wrapper = new VisualElement();
                    wrapper.style.flexDirection = FlexDirection.Row;
                    var a = VRCFuryEditorUtils.PropWithoutLabel(el.FindPropertyRelative("controller"));
                    a.style.flexBasis = 0;
                    a.style.flexGrow = 1;
                    wrapper.Add(a);
                    var b = VRCFuryEditorUtils.PropWithoutLabel(el.FindPropertyRelative("type"));
                    b.style.flexBasis = 0;
                    b.style.flexGrow = 1;
                    wrapper.Add(b);
                    return wrapper;
                }));

            content.Add(VRCFuryEditorUtils.WrappedLabel("Menus + Path Prefix:"));
            content.Add(VRCFuryEditorUtils.WrappedLabel("(If prefix is left empty, menu will be merged into avatar's root menu)"));
            content.Add(VRCFuryEditorUtils.List(prop.FindPropertyRelative("menus"),
                (i, el) => {
                    var wrapper = new VisualElement();
                    wrapper.style.flexDirection = FlexDirection.Row;
                    var a = VRCFuryEditorUtils.PropWithoutLabel(el.FindPropertyRelative("menu"));
                    a.style.flexBasis = 0;
                    a.style.flexGrow = 1;
                    wrapper.Add(a);
                    var b = VRCFuryEditorUtils.PropWithoutLabel(el.FindPropertyRelative("prefix"));
                    b.style.flexBasis = 0;
                    b.style.flexGrow = 1;
                    wrapper.Add(b);
                    return wrapper;
                }));
            
            content.Add(VRCFuryEditorUtils.WrappedLabel("Parameters:"));
            content.Add(VRCFuryEditorUtils.List(prop.FindPropertyRelative("prms"),
                (i, el) => VRCFuryEditorUtils.PropWithoutLabel(el.FindPropertyRelative("parameters"))));
            
            content.Add(VRCFuryEditorUtils.WrappedLabel("Global Parameters:"));
            content.Add(VRCFuryEditorUtils.WrappedLabel(
                "Parameters in this list will have their name kept as is, allowing you to interact with " +
                "parameters in the avatar itself or other instances of the prop. Note that VRChat global " +
                "parameters (such as gestures) are included by default."));
            content.Add(VRCFuryEditorUtils.List(prop.FindPropertyRelative("globalParams"),
                (i,prmProp) => VRCFuryEditorUtils.PropWithoutLabel(prmProp)));
            
            content.Add(VRCFuryEditorUtils.WrappedLabel("Remove prefixes from clips:"));
            content.Add(VRCFuryEditorUtils.WrappedLabel(
                "Strings in this list will be removed from the start of every animated key, useful if the animations" +
                " in the controller were originally written to be based from the avatar root, " +
                "but you are now trying to use as a VRCFury prop."));
            content.Add(VRCFuryEditorUtils.List(prop.FindPropertyRelative("removePrefixes"),
                (i,prmProp) => VRCFuryEditorUtils.PropWithoutLabel(prmProp)));
            
            content.Add(new PropertyField(prop.FindPropertyRelative("allNonsyncedAreGlobal"), "Make all unsynced params global (Legacy mode)"));
            content.Add(new PropertyField(prop.FindPropertyRelative("disableClipRewriting"), "Leave clip keys relative to avatar root (Extremely unusual)"));

            return content;
        }
        
        private static HashSet<string> VRChatGlobalParams = new HashSet<string> {
            "IsLocal",
            "Viseme",
            "Voice",
            "GestureLeft",
            "GestureRight",
            "GestureLeftWeight",
            "GestureRightWeight",
            "AngularY",
            "VelocityX",
            "VelocityY",
            "VelocityZ",
            "Upright",
            "Grounded",
            "Seated",
            "AFK",
            "TrackingType",
            "VRMode",
            "MuteSelf",
            "InStation",
            "AvatarVersion",
            "GroundProximity",
            "VRCEmote"
        };
    }

}
