using System;
using System.Collections.Generic;
using System.Text;
using ZoneSnapper.Engine;

namespace ZoneSnapper.GameSide
{
    /// <summary>
    /// 描边退回原因的计数器。
    ///
    /// 【为什么单独一个类】第三轮实机只有总量 <c>fallback=119 / traceHit=17</c>，读不出「到底是哪一条闸挡的」——
    /// NotOnNetwork（锚点认不出/越界）、CrossesObstacle（跨越闸）、DetourTooLong（绕行上限）这三种的修法完全不同，
    /// 猜一个改一个要浪费玩家一整轮测试。原因在代码里本来就分开返回了（<see cref="TraceFallback"/> 十个值），
    /// 这里只是把它记下来，统计行里输出非零的几项。
    ///
    /// 【为什么不加进 SnapperState】SnapperState 是热路径的 volatile 镜像（Playbook 硬规则 15），
    /// 混一个数组进去容易让人误以为可以跨线程读。这一份只在主线程 ToolUpdate 里写、在统计行里读，
    /// 全部单线程，所以不需要 volatile；数组长度按枚举实际算出来（Playbook：不许硬编码长度）。
    /// </summary>
    public static class FallbackTally
    {
        private static readonly string[] kNames;
        private static readonly long[] m_Counts;

        static FallbackTally()
        {
            // 长度与名字都从枚举本身来：以后加一种回退原因，这里不用改。
            Array values = Enum.GetValues(typeof(TraceFallback));
            int max = 0;
            for (int i = 0; i < values.Length; i++) max = Math.Max(max, (int)values.GetValue(i));
            m_Counts = new long[max + 1];
            kNames = new string[max + 1];
            for (int i = 0; i < values.Length; i++) kNames[(int)values.GetValue(i)] = values.GetValue(i).ToString();
        }

        public static void Note(TraceFallback reason)
        {
            int i = (int)reason;
            if (i >= 0 && i < m_Counts.Length) m_Counts[i]++;
        }

        public static long Of(TraceFallback reason)
        {
            int i = (int)reason;
            return i >= 0 && i < m_Counts.Length ? m_Counts[i] : 0;
        }

        /// <summary>"NotOnNetwork×112,DetourTooLong×4" —— None/Disabled 不进摘要（前者不是回退，后者是玩家关的）。</summary>
        public static string Summary()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < m_Counts.Length; i++)
            {
                if (m_Counts[i] <= 0) continue;
                if (i == (int)TraceFallback.None || i == (int)TraceFallback.Disabled) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kNames[i]).Append('×').Append(m_Counts[i]);
            }
            return sb.Length > 0 ? sb.ToString() : "-";
        }
    }
}
