using System;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 键位路径的纯字符串判据。
    ///
    /// 为什么单独一层：Playbook §3.2 记着一条真实事故 —— 「没绑键的 action 不要 shouldBeEnabled=true」：
    /// 框架给未绑定 action 的默认路径只到设备级（形如 "&lt;Keyboard&gt;/"），输入系统按前缀匹配整块设备，
    /// 于是玩家随便碰一下键盘就触发模组动作。这条判据必须是纯函数，才能在离线壳里用一串路径钉死，
    /// 而不是等实机「模组自己动了」再去猜（BridgeTheLanguageGap 当年就是这么发现的）。
    /// </summary>
    public static class InputKit
    {
        /// <summary>
        /// 这条绑定路径是否真的绑到了一颗具体的键。
        /// 有效："&lt;Keyboard&gt;/f5"、"&lt;Keyboard&gt;/tab"、"&lt;Gamepad&gt;/buttonSouth"、"&lt;Keyboard&gt;/leftshift+f"
        /// 无效：""、null、"&lt;Keyboard&gt;/"、"&lt;Pointer&gt;/"、只有尖括号设备名
        /// </summary>
        public static bool IsBindablePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string s = path.Trim();
            if (s.Length == 0) return false;
            int slash = s.LastIndexOf('/');
            if (slash < 0)
            {
                // 没有 '/' 的裸串：只接受形如 "f5" 这种至少一个字符的控制名
                return IsControlName(s);
            }
            string device = s.Substring(0, slash).Trim();
            string control = s.Substring(slash + 1).Trim();
            if (device.Length == 0) return false;
            if (!IsControlName(control)) return false;
            // 复合键（ctrl+x）里只要最后一段是控制名即可
            return true;
        }

        /// <summary>控制名判据：非空、不含尖括号、不含空白。</summary>
        private static bool IsControlName(string control)
        {
            if (string.IsNullOrEmpty(control)) return false;
            if (control.IndexOf('<') >= 0 || control.IndexOf('>') >= 0) return false;
            if (control.IndexOf(' ') >= 0) return false;
            return control.Length > 0;
        }

        /// <summary>
        /// 路径归一化：切分/去空/去重后按固定顺序拼回。⚠ 必须幂等（跑两次结果相同）——
        /// 绑定的字符串会被「读→改→写→再读」反复处理，不幂等就会每次少一截。
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string s = path.Trim();
            int slash = s.IndexOf('/');
            if (slash < 0) return s;
            string device = s.Substring(0, slash).Trim();
            string rest = s.Substring(slash + 1).Trim();
            if (device.Length == 0) return string.Empty;
            if (rest.Length == 0) return device + "/";
            string[] parts = rest.Split('+');
            System.Collections.Generic.List<string> keep = new System.Collections.Generic.List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                bool dup = false;
                for (int k = 0; k < keep.Count; k++)
                {
                    if (string.Equals(keep[k], p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (!dup) keep.Add(p);
            }
            if (keep.Count == 0) return device + "/";
            return device + "/" + string.Join("+", keep.ToArray());
        }
    }
}
