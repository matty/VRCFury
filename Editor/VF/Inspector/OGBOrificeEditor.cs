using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VF.Model;
using VRC.Dynamics;

namespace VF.Inspector {
    [CustomEditor(typeof(OGBOrifice), true)]
    public class OGBOrificeEditor : Editor {
        public override VisualElement CreateInspectorGUI() {
            var self = (OGBOrifice)target;

            var container = new VisualElement();
            
            container.Add(new PropertyField(serializedObject.FindProperty("name"), "Name Override"));
            container.Add(new PropertyField(serializedObject.FindProperty("addLight"), "Add DPS Light"));
            container.Add(VRCFuryEditorUtils.WrappedLabel("Depth Override in meters (note, this only affects hand touches, penetrators do not use orifice depth)"));
            container.Add(VRCFuryEditorUtils.PropWithoutLabel(serializedObject.FindProperty("length")));

            return container;
        }
        
        [DrawGizmo(GizmoType.Selected | GizmoType.Active | GizmoType.InSelectionHierarchy)]
        static void DrawGizmo(OGBOrifice scr, GizmoType gizmoType) {
            var autoInfo = GetInfoFromLights(scr.gameObject);
            var forward = Vector3.forward;
            if (autoInfo != null) {
                forward = autoInfo.Item1;
            }

            var tightRot = Quaternion.LookRotation(forward) * Quaternion.LookRotation(Vector3.up);

            var c = Handles.color;
            try {
                Handles.color = Color.red;
                var size = GetCapsuleSize(scr);
                var length = size.Item1;
                var radius = size.Item2;
                OGBPenetratorEditor.DrawCapsule(
                    scr.gameObject,
                    forward * -(length / 2),
                    tightRot,
                    length,
                    radius
                );
                Handles.Label(scr.transform.position, "Entrance");
                Handles.Label(scr.transform.TransformPoint(forward * -(length / 2) / scr.transform.lossyScale.x), "Inside");
            } finally {
                Handles.color = c;
            }
        }
        
        public static void Bake(OGBOrifice orifice, List<string> usedNames = null, bool onlySenders = false) {
            if (usedNames == null) usedNames = new List<string>();
            var obj = orifice.gameObject;
            OGBUtils.RemoveTPSSenders(obj);
            
            OGBUtils.AssertValidScale(obj, "orifice");

            var autoInfo = GetInfoFromLights(obj);

            var forward = Vector3.forward;
            if (autoInfo != null) {
                forward = autoInfo.Item1;
            }

            var name = orifice.name;
            if (string.IsNullOrWhiteSpace(name)) {
                name = obj.name;
            }

            Debug.Log("Baking OGB " + obj + " as " + name);

            // Senders
            OGBUtils.AddSender(obj, Vector3.zero, "Root", 0.01f, OGBUtils.CONTACT_ORF_MAIN);
            OGBUtils.AddSender(obj, forward * 0.01f, "Front", 0.01f, OGBUtils.CONTACT_ORF_NORM);
            
            var paramPrefix = OGBUtils.GetNextName(usedNames, "OGB/Orf/" + name.Replace('/','_'));

            if (onlySenders) {
                var bake = new GameObject("OGB_Baked_Orf");
                bake.transform.SetParent(obj.transform, false);
                if (!string.IsNullOrWhiteSpace(orifice.name)) {
                    var nameObj = new GameObject("name=" + orifice.name);
                    nameObj.transform.SetParent(bake.transform, false);
                }
            } else {
                // Receivers
                var size = GetCapsuleSize(orifice);
                var oscDepth = size.Item1;
                var closeRadius = size.Item2;
                var tightRot = Quaternion.LookRotation(forward) * Quaternion.LookRotation(Vector3.up);
                var frotRadius = 0.1f;
                var frotPos = 0.05f;
                OGBUtils.AddReceiver(obj, forward * -oscDepth, paramPrefix + "/TouchSelf", "TouchSelf", oscDepth, OGBUtils.SelfContacts, allowOthers:false, localOnly:true);
                OGBUtils.AddReceiver(obj, forward * -(oscDepth/2), paramPrefix + "/TouchSelfClose", "TouchSelfClose", closeRadius, OGBUtils.SelfContacts, allowOthers:false, localOnly:true, height: oscDepth, rotation: tightRot, type: ContactReceiver.ReceiverType.Constant);
                OGBUtils.AddReceiver(obj, forward * -oscDepth, paramPrefix + "/TouchOthers", "TouchOthers", oscDepth, OGBUtils.BodyContacts, allowSelf:false, localOnly:true);
                OGBUtils.AddReceiver(obj, forward * -(oscDepth/2), paramPrefix + "/TouchOthersClose", "TouchOthersClose", closeRadius, OGBUtils.BodyContacts, allowSelf:false, localOnly:true, height: oscDepth, rotation: tightRot, type: ContactReceiver.ReceiverType.Constant);
                OGBUtils.AddReceiver(obj, Vector3.zero, paramPrefix + "/PenSelfNewRoot", "PenSelfNewRoot", 1f, new []{OGBUtils.CONTACT_PEN_ROOT}, allowOthers:false, localOnly:true);
                OGBUtils.AddReceiver(obj, Vector3.zero, paramPrefix + "/PenSelfNewTip", "PenSelfNewTip", 1f, new []{OGBUtils.CONTACT_PEN_MAIN}, allowOthers:false, localOnly:true);
                OGBUtils.AddReceiver(obj, forward * -oscDepth, paramPrefix + "/PenOthers", "PenOthers", oscDepth, new []{OGBUtils.CONTACT_PEN_MAIN}, allowSelf:false, localOnly:true);
                OGBUtils.AddReceiver(obj, forward * -(oscDepth/2), paramPrefix + "/PenOthersClose", "PenOthersClose", closeRadius, new []{OGBUtils.CONTACT_PEN_MAIN}, allowSelf:false, localOnly:true, height: oscDepth, rotation: tightRot, type: ContactReceiver.ReceiverType.Constant);
                OGBUtils.AddReceiver(obj, Vector3.zero, paramPrefix + "/PenOthersNewRoot", "PenOthersNewRoot", 1f, new []{OGBUtils.CONTACT_PEN_ROOT}, allowSelf:false, localOnly:true);
                OGBUtils.AddReceiver(obj, Vector3.zero, paramPrefix + "/PenOthersNewTip", "PenOthersNewTip", 1f, new []{OGBUtils.CONTACT_PEN_MAIN}, allowSelf:false, localOnly:true);
                OGBUtils.AddReceiver(obj, forward * frotPos, paramPrefix + "/FrotOthers", "FrotOthers", frotRadius, new []{OGBUtils.CONTACT_ORF_MAIN}, allowSelf:false, localOnly:true);
            }
            
            OGBUtils.AddVersionContacts(obj, paramPrefix, onlySenders, false);

            if (autoInfo == null && orifice.addLight != AddLight.None) {
                foreach (var light in obj.GetComponentsInChildren<Light>(true)) {
                    OGBUtils.RemoveComponent(light);
                }

                var main = new GameObject("Root");
                main.transform.SetParent(obj.transform, false);
                var mainLight = main.AddComponent<Light>();
                mainLight.type = LightType.Point;
                mainLight.color = Color.black;
                mainLight.range = orifice.addLight == AddLight.Ring ? 0.42f : 0.41f;
                mainLight.shadows = LightShadows.None;
                mainLight.renderMode = LightRenderMode.ForceVertex;

                var front = new GameObject("Front");
                front.transform.SetParent(obj.transform, false);
                var frontLight = front.AddComponent<Light>();
                front.transform.localPosition = new Vector3(0, 0, 0.01f / obj.transform.lossyScale.x);
                frontLight.type = LightType.Point;
                frontLight.color = Color.black;
                frontLight.range = 0.45f;
                frontLight.shadows = LightShadows.None;
                frontLight.renderMode = LightRenderMode.ForceVertex;
            }
            
            DestroyImmediate(orifice);
        }

        private static Tuple<float, float> GetCapsuleSize(OGBOrifice orifice) {
            var length = orifice.length;
            if (length <= 0) length = 0.25f;
            var radius = length / 2.5f;
            return Tuple.Create(length, radius);
        }
        
        private static bool IsHole(Light light) {
            var rangeId = light.range % 0.1;
            return rangeId >= 0.005f && rangeId < 0.015f;
        }
        private static bool IsRing(Light light) {
            var rangeId = light.range % 0.1;
            return rangeId >= 0.015f && rangeId < 0.025f;
        }
        private static bool IsNormal(Light light) {
            var rangeId = light.range % 0.1;
            return rangeId >= 0.045f && rangeId <= 0.055f;
        }
        
        public static Tuple<Vector3, bool> GetInfoFromLights(GameObject obj) {
            var isRing = false;
            Light main = null;
            Light normal = null;
            foreach (Transform child in obj.transform) {
                var light = child.gameObject.GetComponent<Light>();
                if (light != null) {
                    if (main == null) {
                        if (IsHole(light)) {
                            main = light;
                        } else if (IsRing(light)) {
                            main = light;
                            isRing = true;
                        }
                    }
                    if (normal == null && IsNormal(light)) {
                        normal = light;
                    }
                }
            }

            if (main == null || normal == null) return null;

            var forward = Vector3.forward;
            if (normal != null) {
                forward = (normal.transform.localPosition - main.transform.localPosition).normalized;
            }

            return Tuple.Create(forward, isRing);
        }
    }
}