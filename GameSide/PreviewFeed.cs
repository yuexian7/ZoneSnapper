using System;
using System.Collections.Generic;
using System.Text;
using ZoneSnapper.Engine;

namespace ZoneSnapper.GameSide
{
    /// <summary>
    /// 实时预览的信箱：生产者在 ToolUpdate（<c>ZoneSnapperSystem</c>，算完贴合与描边顺手发布），
    /// 消费者在 Rendering（<c>ZoneSnapperPreviewSystem</c>，淡色画出来）。第四轮反馈 2 的接线处。
    ///
    /// 【为什么不加锁】两头都跑在主线程的系统更新里，相位有先后 ⇒ 不存在并发。
    /// 但**读写跨越了帧**（Rendering 相位排在 ToolUpdate 之前），所以约定一条：
    /// 发布出去的那个 <see cref="PreviewKit.Result"/> 从此只读，生产者下一帧要算就 new 一个新的换上去。
    /// 这样消费者拿到的永远是一整套算完的几何，不会是「线段填了一半、点还没加」的中间态。
    ///
    /// 【为什么单独一个类而不塞进 SnapperState】SnapperState 是热路径的 volatile 数值镜像
    /// （Playbook 硬规则 15），往里塞引用类型容易让人误以为可以跨线程读。
    /// </summary>
    public static class PreviewFeed
    {
        /// <summary>渲染方读这一格；null = 这一帧没有预览。</summary>
        public static PreviewKit.Result Current;

        /// <summary>发布过多少次「有内容」的预览（实机看它涨不涨就知道预览链路通没通）。</summary>
        public static long Publishes;

        /// <summary>渲染方真正画了多少帧。</summary>
        public static long Draws;

        /// <summary>最近一次「为什么没有预览」。</summary>
        public static string LastSkip;

        /// <summary>
        /// 最后一次「提交或撤销预览」发生在全局第几帧（<c>UnityEngine.Time.frameCount</c>）。
        /// 为什么不用我们自己的帧计数：生产端整个停摆时（工具被 ESC 关掉、相位没被驱动、异常兜底）
        /// 我们自己的计数器也跟着停 ⇒ 永远看不出「已经不新鲜了」。用游戏的全局帧号才能识破。
        /// 第五轮反馈 1 的第二道保险，第一道在生产端（所有提前返回都走 Idle）。
        /// </summary>
        public static long PublishedAtFrame;

        private static readonly Dictionary<string, long> m_Skips = new Dictionary<string, long>(16, StringComparer.Ordinal);

        public static void Publish(PreviewKit.Result r)
        {
            PublishedAtFrame = UnityEngine.Time.frameCount;
            if (r == null || !r.HasContent)
            {
                Current = null;
                NoteSkip(r != null && !string.IsNullOrEmpty(r.Skip) ? r.Skip : "empty");
                return;
            }
            Current = r;
            Publishes++;
        }

        public static void Clear(string why)
        {
            PublishedAtFrame = UnityEngine.Time.frameCount;
            Current = null;
            if (!string.IsNullOrEmpty(why)) NoteSkip(why);
        }

        /// <summary>
        /// 这份预览还是这一帧/上一两帧算出来的吗？超过 <paramref name="maxFrames"/> 帧就说明生产端
        /// 已经不跑了（它停摆的那条路径往往是「工具被切走」，正是玩家看到淡色线留在地上的那一刻）。
        /// </summary>
        public static bool IsFresh(int maxFrames)
        {
            if (Current == null || !Current.HasContent) return false;
            return UnityEngine.Time.frameCount - PublishedAtFrame <= maxFrames;
        }

        private static void NoteSkip(string why)
        {
            LastSkip = why;
            long v;
            m_Skips.TryGetValue(why, out v);
            m_Skips[why] = v + 1;
        }

        /// <summary>"budget×231,preview-off×8" —— 玩家说「预览有时不出来」就读这一串。</summary>
        public static string SkipSummary()
        {
            if (m_Skips.Count == 0) return "-";
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, long> kv in m_Skips)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key).Append('×').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>工具切走 / 会话结束：把画面上的预览一起撤掉，账不清（累计量要留着看趋势）。</summary>
        public static void Drop()
        {
            Current = null;
        }

        /// <summary>玩家按「重算」时的完全复位，连账一起清。</summary>
        public static void Reset()
        {
            Current = null;
            Publishes = 0;
            Draws = 0;
            LastSkip = null;
            m_Skips.Clear();
        }
    }
}
