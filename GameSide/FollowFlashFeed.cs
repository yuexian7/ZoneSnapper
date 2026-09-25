using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.GameSide
{
    /// <summary>
    /// 「刚被自动跟随改过」的高亮信箱（第五轮反馈 8）。
    ///
    /// 【玩家要的是什么】原话：「显示贴合提示 没看懂什么意思，是要提示节点被调整到哪了吗？
    /// 那你高亮被调整过的区域的边界，这些调整过的节点闪烁一会，然后过一会消失。」
    /// 上一档那个开关叫「显示贴合提示」，做的却是「画当前吸附来源」这种没人看得懂的东西 ⇒ 整条换掉：
    /// 跟随改写了哪块区域，就把那一圈边界描出来 + 把被挪动的节点闪几下，若干秒后自己消失。
    ///
    /// 【为什么单独一个信箱而不塞进 PreviewFeed】两者生命周期不同：
    ///  · 预览是「每一帧重算、当帧作废」；
    ///  · 高亮是「一次事件之后自己活 N 秒」，而且预览可以被玩家整个关掉，跟随的反馈不能跟着一起没 ——
    ///    它改的是玩家的存档，玩家必须看得见它动了哪里。
    /// 渲染端复用同一个 OverlayRenderSystem 通道（<see cref="Systems.ZoneSnapperPreviewSystem"/>），
    /// 因为那是唯一一条不需要补丁就能画线的路。
    /// </summary>
    public static class FollowFlashFeed
    {
        /// <summary>一条高亮：整圈边界 + 其中被挪动的那几格。</summary>
        public sealed class Flash
        {
            public readonly List<P3> Ring = new List<P3>();
            public readonly List<P3> Moved = new List<P3>();

            /// <summary>消失时刻（<c>UnityEngine.Time.unscaledTime</c> 秒）。用非缩放时间：暂停菜单里也不该续命。</summary>
            public float Until;

            /// <summary>这一条来自哪次操作（诊断用）：FOLLOW=路网变更跟随，DRAG=拖拽落档，MANUAL=快捷键重算。</summary>
            public string Source;
        }

        /// <summary>一次事件最多记几块区域的账（跟随一批可能改十几块，全画出来反而看不清）。</summary>
        public const int MAX_FLASHES = 8;

        /// <summary>高亮停留多久（秒）。玩家的要求是「闪烁一会，然后过一会消失」。</summary>
        public const float LIFE_SECONDS = 4.0f;

        private static readonly List<Flash> m_Flashes = new List<Flash>(MAX_FLASHES);

        /// <summary>累计记了几条（实机判读：跟随确实写了东西而玩家说没看到，就看这一格与 Draws 是否同量级）。</summary>
        public static long Notified;

        public static void Add(IList<P3> ring, IList<P3> moved, string source)
        {
            if (ring == null || ring.Count < 3) return;
            Expire();
            if (m_Flashes.Count >= MAX_FLASHES) m_Flashes.RemoveAt(0);     // 最旧的那条先让位
            Flash f = new Flash();
            for (int i = 0; i < ring.Count; i++) f.Ring.Add(ring[i]);
            if (moved != null) for (int i = 0; i < moved.Count; i++) f.Moved.Add(moved[i]);
            f.Until = UnityEngine.Time.unscaledTime + LIFE_SECONDS;
            f.Source = source;
            m_Flashes.Add(f);
            Notified++;
        }

        /// <summary>渲染端读：当前还在有效期内的所有高亮（顺序不敏感）。</summary>
        public static List<Flash> Active()
        {
            Expire();
            return m_Flashes;
        }

        public static bool HasContent
        {
            get { Expire(); return m_Flashes.Count > 0; }
        }

        public static void Clear()
        {
            m_Flashes.Clear();
        }

        private static void Expire()
        {
            float now = UnityEngine.Time.unscaledTime;
            for (int i = m_Flashes.Count - 1; i >= 0; i--)
            {
                if (m_Flashes[i].Until <= now) m_Flashes.RemoveAt(i);
            }
        }
    }
}
