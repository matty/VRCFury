using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace VF.Builder {
    
    /**
     * This is responsible for fixing pagination for menus before / after VRCFury is done with them.
     * It is capable of splitting menus with more than the maximum allowed controls into separate pages,
     * and also capable of re-joining them back into oversized menus again.
     */
    public class MenuSplitter {
        public static void SplitMenus(VRCExpressionsMenu menu) {
            if (menu == null) return;
            if (menu.controls.Count > VRCExpressionsMenu.MAX_CONTROLS) {
                var nextPath = GetNextPageFilename(menu);
                var nextMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                AssetDatabase.CreateAsset(nextMenu, nextPath);

                while (menu.controls.Count > VRCExpressionsMenu.MAX_CONTROLS - 1) {
                    nextMenu.controls.Insert(0, menu.controls[menu.controls.Count-1]);
                    menu.controls.RemoveAt(menu.controls.Count-1);
                }
                menu.controls.Add(new VRCExpressionsMenu.Control() {
                    name = "Next",
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    subMenu = nextMenu
                });
            }
            foreach (var control in menu.controls) {
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu) {
                    SplitMenus(control.subMenu);
                }
            }
        }

        private static string GetNextPageFilename(VRCExpressionsMenu menu) {
            var assetPath = AssetDatabase.GetAssetPath(menu);
            if (assetPath == null) {
                throw new Exception(
                    "Needed to split menu " + menu.name + ", but it doesn't seem to be saved to a file?");
            }

            var dirName = Path.GetDirectoryName(assetPath);
            var baseName = Path.GetFileNameWithoutExtension(assetPath);
            var vfp = baseName.IndexOf("_vfp");
            if (vfp >= 0) {
                baseName = baseName.Substring(0, vfp);
            }
            for (var i = 2;; i++) {
                var newPath = dirName + "/" + baseName + "_vfp" + i + ".asset";
                if (File.Exists(newPath)) {
                    continue;
                }
                return newPath;
            }
        }
        
        public static void JoinMenus(VRCExpressionsMenu menu) {
            if (menu == null) return;
            for (var i = 0; i < menu.controls.Count; i++) {
                var item = menu.controls[i];
                if (IsVrcfPageItem(item)) {
                    menu.controls.RemoveAt(i);
                    menu.controls.InsertRange(i, item.subMenu.controls);
                    AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(item.subMenu));
                    i--;
                    continue;
                }
                if (item.type == VRCExpressionsMenu.Control.ControlType.SubMenu) {
                    JoinMenus(item.subMenu);
                }
            }
        }

        private static bool IsVrcfPageItem(VRCExpressionsMenu.Control item) {
            if (item.type != VRCExpressionsMenu.Control.ControlType.SubMenu) return false;
            if (item.subMenu == null) return false;
            var assetPath = AssetDatabase.GetAssetPath(item.subMenu);
            if (assetPath == null) return false;
            return assetPath.Contains("_vfp");
        }
    }
}