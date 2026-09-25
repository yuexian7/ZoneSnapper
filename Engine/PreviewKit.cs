using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 实时预览（第四轮反馈 2）：**这一帧点下去 / 松手之后会成什么样**。
    ///
    /// 【为什么不直接把预览点写进游戏的控制点表】
    ///  ① 倒数第二格 ↔ 游标那一格要过游戏的最小间距校验（FACT：AreaToolSystem.cs:3509-3511），
    ///    预览点一旦占进表里，玩家最后一次点击会被静默吃掉 —— 这正是 v0.1.1 闭合边折返线的形状；
    ///  ② 左键落点时游戏把当时表里的点**全数**收进区域 ⇒ 预览点会变成真节点，
    ///    而玩家随时可能把鼠标移开不点。预览必须是可丢弃的。
    /// ⇒ 预览只画（<c>Systems/ZoneSnapperPreviewSystem.cs</c> 走 OverlayRenderSystem 的淡色线），
    ///   控制点表的形状一个字节都不动。这也是它「颜色更淡」的技术原因：它压根不是同一条通道。
    ///
    /// 【性能口径】预览每帧都算，所以带一道预算闸：上一帧整步超过 <see cref="BUDGET_MS"/> 毫秒
    /// 就这一帧不重算、沿用上一帧那套几何（预览晚一帧不伤人，画着手会）。
    /// 两个入口都**只读不动**输入：手动栈、存档环、世界快照一律原样回去 —— 预览没有资格改任何已落档的东西。
    /// </summary>
    public static class PreviewKit
    {
        /// <summary>整步预算（毫秒）。超过就跳过这一帧的预览重算（见类注释的性能口径）。</summary>
        public const double BUDGET_MS = 4.0;

        /// <summary>一次预览的结果：<see cref="Segments"/> 淡色折线 + <see cref="Dots"/> 淡色节点。</summary>
        public sealed class Result
        {
            /// <summary>每条预览折线，含两端：起点是已落档的那一格，终点是将要落下的一格。</summary>
            public readonly List<Polyline> Segments = new List<Polyline>();

            /// <summary>将要吸附到的那一格 + 会被自动插入的那些格（顺序不敏感，渲染只是画圈）。</summary>
            public readonly List<P3> Dots = new List<P3>();

            /// <summary>为什么这一帧没有预览（诊断；进统计行）。null = 正常有内容。</summary>
            public string Skip;

            /// <summary>预览那条边的回退原因（None = 真的沿网络走通了）。玩家问「为什么不贴」先看这一格。</summary>
            public TraceFallback Fallback = TraceFallback.None;

            /// <summary>线宽与节点圈直径（米）。由游戏侧按 prefab 自己的 m_SnapDistance 换算后填进来。</summary>
            public float LineWidth = 0.3f;
            public float DotDiameter = 1.2f;

            public bool HasContent { get { return Segments.Count > 0 || Dots.Count > 0; } }
        }

        /// <summary>
        /// 绘制中：最后一格 → 游标（也就是「下一个准备放置的节点」）。
        /// <paramref name="cursor"/> 必须是调用方这一帧真正算出来的吸附结果 ——
        /// 这里不再 FindSnap 第二遍：每帧两次图搜索是白给的开销，而且两次结果理论上能不一致。
        /// </summary>
        public static Result Stroke(IList<PlacedNode> anchors, PlacedNode cursor, WorldSnapshot world,
                                   ModConfig cfg, ResolvedTuning tuning, double lastFrameMs)
        {
            Result r = new Result();
            r.Skip = Gate(cfg, world, tuning, lastFrameMs);
            if (r.Skip != null) return r;

            // 第一格：还没有任何已放下的节点 ⇒ 预览只剩「你会落在这里」那一个圈。
            if (anchors == null || anchors.Count == 0)
            {
                r.Dots.Add(cursor.Pos);
                return r;
            }

            r.Dots.Add(cursor.Pos);
            if (!tuning.TraceEnabled) { r.Skip = "trace-off"; return r; }

            PlacedNode from = anchors[anchors.Count - 1];
            TraceContext ctx = new TraceContext();
            TraceContextUtil.FillWithCursor(anchors, cursor.Pos, ref ctx);
            ctx.HasRingEdge = true;
            ctx.RingEdge = anchors.Count - 1;     // 「最后一格 → 游标」这一格（游标已补进环尾）

            TraceResult tr = TraceKit.Trace(from, cursor, world, cfg, tuning, ctx);
            r.Fallback = tr.Fallback;
            AddSegment(r.Segments, from.Pos, tr.Points, cursor.Pos);
            for (int i = 0; i < tr.Points.Count; i++) r.Dots.Add(tr.Points[i]);
            if (!r.HasContent) r.Skip = "no-line";
            return r;
        }

        /// <summary>
        /// 拖拽改形：玩家抓着已存在区域的一格还没松手。
        /// 要看的是**和它左右相邻的那两格**，其余每一格每一条边都不许动
        /// （需求 3 的第二种模式；实现复用 <see cref="FollowKit.Around"/>，与松手后重描同一段逻辑）。
        /// </summary>
        /// <param name="ring">存档里这一圈节点的当前位置（还没含拖动，按原样传进来即可）。</param>
        /// <param name="nodeIndex">被拖的是第几格。来源必须是游戏自己的口径，见 ZoneSnapperSystem 的取证注释。</param>
        /// <param name="livePos">这一帧被拖到的位置（游戏的游标，已经过游戏自己的吸附）。</param>
        public static Result Drag(IList<P3> ring, bool closed, int nodeIndex, P3 livePos,
                                  WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, double lastFrameMs)
        {
            Result r = new Result();
            r.Skip = Gate(cfg, world, tuning, lastFrameMs);
            if (r.Skip != null) return r;
            if (ring == null || ring.Count < 3 || nodeIndex < 0 || nodeIndex >= ring.Count)
            {
                r.Skip = "bad-index";
                return r;
            }

            // 把「拖到哪」当成环里那一格的当前值：Around 只认环，不认游戏的拖拽中间态。
            // 必须复制一份 —— 传进来的 ring 是从存档缓冲读出来的，原地改等于改玩家的区域。
            List<P3> live = new List<P3>(ring.Count);
            for (int i = 0; i < ring.Count; i++) live.Add(i == nodeIndex ? livePos : ring[i]);

            FollowKit.DragResult d = FollowKit.Around(live, closed, nodeIndex, world, cfg, tuning);
            if (d == null) { r.Skip = "no-drag"; return r; }

            r.Dots.Add(d.Dragged.Pos);
            if (d.PrevIndex >= 0)
            {
                r.Fallback = d.BeforeFallback;
                AddSegment(r.Segments, d.Prev.Pos, d.Before, d.Dragged.Pos);
                AddDots(r.Dots, d.Before);
            }
            if (d.NextIndex >= 0)
            {
                AddSegment(r.Segments, d.Dragged.Pos, d.After, d.Next.Pos);
                AddDots(r.Dots, d.After);
                if (d.PrevIndex < 0) r.Fallback = d.AfterFallback;
            }
            if (!r.HasContent) r.Skip = "no-line";
            return r;
        }

        /// <summary>公共闸门：预览是「锦上添花」，任何一条不满足都只是不画，绝不影响落档本身。</summary>
        private static string Gate(ModConfig cfg, WorldSnapshot world, ResolvedTuning tuning, double lastFrameMs)
        {
            if (cfg == null || !cfg.Enabled) return "mod-off";
            if (!cfg.ShowLivePreview) return "preview-off";
            if (world == null) return "no-world";
            if (!tuning.SnapEnabled) return "snap-off";
            if (lastFrameMs > BUDGET_MS) return "budget";
            return null;
        }

        private static void AddDots(List<P3> dots, List<P3> pts)
        {
            if (pts == null) return;
            for (int i = 0; i < pts.Count; i++) dots.Add(pts[i]);
        }

        /// <summary>拼一条预览折线：a → 中间点… → b，丢掉重合的相邻点（渲染时长为零的段会被游戏那边直接丢弃）。</summary>
        private static void AddSegment(List<Polyline> segs, P3 a, IList<P3> middle, P3 b)
        {
            Polyline p = new Polyline();
            p.Add(a);
            if (middle != null)
            {
                for (int i = 0; i < middle.Count; i++)
                {
                    if (p.Points[p.Points.Count - 1].DistanceTo(middle[i]) <= 1e-6) continue;
                    p.Add(middle[i]);
                }
            }
            if (p.Points[p.Points.Count - 1].DistanceTo(b) > 1e-6) p.Add(b);
            if (!p.IsEmpty) segs.Add(p);
        }
    }
}
