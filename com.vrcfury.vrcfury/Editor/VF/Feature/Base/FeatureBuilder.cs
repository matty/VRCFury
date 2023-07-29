using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VF.Builder;
using VF.Inspector;
using VF.Model;
using VF.Model.Feature;
using VF.Model.StateAction;
using VF.Utils;
using VRC.SDK3.Avatars.Components;

namespace VF.Feature.Base {
    public abstract class FeatureBuilder {
        [JsonProperty(Order = -2)] public string type;
        [NonSerialized] [JsonIgnore] public AvatarManager manager;
        [NonSerialized] [JsonIgnore] public ClipBuilder clipBuilder;
        [NonSerialized] [JsonIgnore] public string tmpDirParent;
        [NonSerialized] [JsonIgnore] public string tmpDir;
        [NonSerialized] [JsonIgnore] public VFGameObject avatarObject;
        [NonSerialized] [JsonIgnore] public VFGameObject originalObject;
        [NonSerialized] [JsonIgnore] public VFGameObject featureBaseObject;
        [NonSerialized] [JsonIgnore] public Action<FeatureModel> addOtherFeature;
        [NonSerialized] [JsonIgnore] public int uniqueModelNum;
        [NonSerialized] [JsonIgnore] public List<FeatureModel> allFeaturesInRun;
        [NonSerialized] [JsonIgnore] public List<FeatureBuilder> allBuildersInRun;
        [NonSerialized] [JsonIgnore] public MutableManager mutableManager;
        [NonSerialized] [JsonIgnore] public Dictionary<(string, VRCAvatarDescriptor.AnimLayerType, string), VFALayer> exclusiveAnimationLayers;
        [NonSerialized] [JsonIgnore] public Dictionary<string, VFALayer> exclusiveParameterLayers;

        public virtual string GetEditorTitle() {
            return null;
        }

        public virtual VisualElement CreateEditor(SerializedProperty prop) {
            return VRCFuryEditorUtils.WrappedLabel("No body");
        }
    
        public virtual bool AvailableOnAvatar() {
            return true;
        }

        public virtual bool AvailableOnProps() {
            return true;
        }
        
        public virtual bool ShowInMenu() {
            return true;
        }

        public ControllerManager GetFx() {
            return manager.GetController(VRCAvatarDescriptor.AnimLayerType.FX);
        }

        public ControllerManager GetAction() {
            return manager.GetController(VRCAvatarDescriptor.AnimLayerType.Action);
        }

        public ControllerManager GetGesture() {
            return manager.GetController(VRCAvatarDescriptor.AnimLayerType.Gesture);
        }

        public ControllerManager GetBase() {
            return manager.GetController(VRCAvatarDescriptor.AnimLayerType.Base);
        }

        protected VFABool CreatePhysBoneResetter(List<GameObject> resetPhysbones, string name) {
            if (resetPhysbones == null || resetPhysbones.Count == 0) return null;

            var fx = GetFx();
            var layer = fx.NewLayer(name + " (PhysBone Reset)");
            var param = fx.NewTrigger(name + "_PhysBoneReset");
            var idle = layer.NewState("Idle");
            var pause = layer.NewState("Pause");
            var reset1 = layer.NewState("Reset").Move(pause, 1, 0);
            var reset2 = layer.NewState("Reset").Move(idle, 1, 0);
            idle.TransitionsTo(pause).When(param.IsTrue());
            pause.TransitionsTo(reset1).When(fx.Always());
            reset1.TransitionsTo(reset2).When(fx.Always());
            reset2.TransitionsTo(idle).When(fx.Always());

            var resetClip = fx.NewClip("Physbone Reset");
            foreach (var physBone in resetPhysbones) {
                if (physBone == null) {
                    Debug.LogWarning("Physbone object in physboneResetter is missing!: " + name);
                    continue;
                }
                clipBuilder.Enable(resetClip, physBone, false);
            }

            reset1.WithAnimation(resetClip);
            reset2.WithAnimation(resetClip);

            return param;
        }

        protected static bool StateExists(State state) {
            return state != null;
        }

        protected AnimationClip LoadState(string name, State state, bool checkForProxy) {
            return LoadState(name, state, null, checkForProxy);
        }
        protected AnimationClip LoadState(string name, State state, VFGameObject animObjectOverride = null, bool checkForProxy = false) {
            if (state == null || state.actions.Count == 0) {
                return GetFx().GetNoopClip();
            }

            var pathRewriter = ClipRewriter.CreateNearestMatchPathRewriter(
                animObject: animObjectOverride ?? featureBaseObject,
                rootObject: avatarObject
            );

            bool IsProxy(Model.StateAction.Action action) {
                if (!(action is AnimationClipAction)) return false;
                AnimationClip c = (action as AnimationClipAction).clip;
                return c.name.Contains("proxy_");
            }
        
            if (state == null) {
                return GetFx().GetNoopClip();
            }

            if (checkForProxy) {
                foreach (var action in state.actions.OfType<AnimationClipAction>()) {
                    if (IsProxy(action)) {
                        AnimationClip c = action.clip;
                        return c;
                    }
                }
            }

            var actionsToAdd = state.actions.Where(action => !IsProxy(action));

            if (actionsToAdd.Count() == 0) {
                return GetFx().GetNoopClip();
            }

            var clip = GetFx().NewClip(name);
            var restingStateBuilder = GetBuilder<RestingStateBuilder>();

            AnimationClip firstClip = actionsToAdd
                .OfType<AnimationClipAction>()
                .Select(action => action.clip)
                .FirstOrDefault();
            var offClip = new AnimationClip();
            var onClip = GetFx().NewClip(name);

            if (firstClip) {
                var nameBak = onClip.name;
                EditorUtility.CopySerialized(firstClip, onClip);
                onClip.name = nameBak;
                onClip.RewritePaths(pathRewriter);
            }

            foreach (var action in actionsToAdd) {
                switch (action) {
                    case FlipbookAction flipbook:
                        if (flipbook.obj != null) {
                            // If we animate the frame to a flat number, unity can internally do some weird tweening
                            // which can result in it being just UNDER our target, (say 0.999 instead of 1), resulting
                            // in unity displaying frame 0 instead of 1. Instead, we target framenum+0.5, so there's
                            // leniency around it.
                            var frameAnimNum = (float)(Math.Floor((double)flipbook.frame) + 0.5);
                            var binding = EditorCurveBinding.FloatCurve(
                                clipBuilder.GetPath(flipbook.obj),
                                typeof(SkinnedMeshRenderer),
                                "material._FlipbookCurrentFrame"
                            );
                            onClip.SetConstant(binding, frameAnimNum);
                        }
                        break;
                    case ShaderInventoryAction shaderInventoryAction: {
                        var renderer = shaderInventoryAction.renderer;
                        if (renderer != null) {
                            var binding = EditorCurveBinding.FloatCurve(
                                clipBuilder.GetPath(renderer.gameObject),
                                renderer.GetType(),
                                $"material._InventoryItem{shaderInventoryAction.slot:D2}Animated"
                            );
                            offClip.SetConstant(binding, 0);
                            onClip.SetConstant(binding, 1);
                        }
                        break;
                    }
                    case AnimationClipAction clipAction:
                        AnimationClip clipActionClip = clipAction.clip;
                        if (clipActionClip && clipActionClip != firstClip) {
                            var copy = mutableManager.CopyRecursive(clipActionClip, "Copy of " + clipActionClip.name);
                            copy.RewritePaths(pathRewriter);
                            onClip.CopyFrom(copy);
                            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(copy));
                        }
                        break;
                    case ObjectToggleAction toggle:
                        if (toggle.obj == null) {
                            Debug.LogWarning("Missing object in action: " + name);
                        } else {
                            clipBuilder.Enable(offClip, toggle.obj, toggle.obj.activeSelf);
                            clipBuilder.Enable(onClip, toggle.obj, !toggle.obj.activeSelf);
                        }
                        break;
                    case BlendShapeAction blendShape:
                        var foundOne = false;
                        foreach (var skin in avatarObject.GetComponentsInSelfAndChildren<SkinnedMeshRenderer>()) {
                            if (!skin.sharedMesh) continue;
                            var blendShapeIndex = skin.sharedMesh.GetBlendShapeIndex(blendShape.blendShape);
                            if (blendShapeIndex < 0) continue;
                            foundOne = true;
                            //var defValue = skin.GetBlendShapeWeight(blendShapeIndex);
                            clipBuilder.BlendShape(onClip, skin, blendShape.blendShape, blendShape.blendShapeValue);
                        }
                        if (!foundOne) {
                            Debug.LogWarning("BlendShape not found in avatar: " + blendShape.blendShape);
                        }
                        break;
                    case ScaleAction scaleAction:
                        if (scaleAction.obj == null) {
                            Debug.LogWarning("Missing object in action: " + name);
                        } else {
                            var localScale = scaleAction.obj.transform.localScale;
                            var newScale = localScale * scaleAction.scale;
                            clipBuilder.Scale(offClip, scaleAction.obj, localScale);
                            clipBuilder.Scale(onClip, scaleAction.obj, newScale);
                        }
                        break;
                    case MaterialAction matAction:
                        if (matAction.obj == null) {
                            Debug.LogWarning("Missing object in action: " + name);
                            break;
                        }
                        if (matAction.mat == null) {
                            Debug.LogWarning("Missing material in action: " + name);
                            break;
                        }
                        clipBuilder.Material(onClip, matAction.obj, matAction.materialIndex, matAction.mat);
                        break;
                }
            }

            restingStateBuilder.ApplyClipToRestingState(offClip);

            return onClip;
        }

        public List<FeatureBuilderAction> GetActions() {
            var list = new List<FeatureBuilderAction>();
            foreach (var method in GetType().GetMethods()) {
                var attr = method.GetCustomAttribute<FeatureBuilderActionAttribute>();
                if (attr == null) continue;
                list.Add(new FeatureBuilderAction(attr, method, this));
            }
            return list;
        }

        public virtual string GetClipPrefix() {
            return null;
        }

        public T GetBuilder<T>() {
            return allBuildersInRun.OfType<T>().First();
        }
    }

    public abstract class FeatureBuilder<ModelType> : FeatureBuilder where ModelType : FeatureModel {
        [NonSerialized] [JsonIgnore] public ModelType model;
    }
}
