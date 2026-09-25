using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// P：PreviewKit —— 实时预览（第四轮反馈 2「点下去之前先看到会贴成什么样」）。
    ///
    /// 【这一层为什么单独有断言】预览走的是渲染通道，T4 实机只能靠眼睛判，
    /// 所以能关在 Engine 里的部分一条都不许留在实机验：
    ///  ① 预览几何必须**含两端**（渲染画的是折线，缺一端就出现「线头悬在玩家指尖前一米」）；
    ///  ② 预览不许碰任何已落档的东西（手动栈、存档环、世界快照全部原样回去）；
    ///  ③ 闸门（开关/预算/无快照）必须是「什么都不画」，而不是「画半套」；
    ///  ④ 拖拽预览的两段必须接在**同一个**被拖格上（两侧各画一条 = 玩家看到两个角）。
    /// ①②③④ 每一条都对应一种「实机看着像坏了、其实只差一个位置」的形状。
    /// </summary>
    internal static partial class Tests
    {
        internal static class Preview
        {
            public static void Run()
            {
                StrokeSections();
                DragSections();
            }

            // —————————————————————————— 绘制中

            private static void StrokeSections()
            {
                Harness.Section("P1 绘制中的预览：一条含两端的折线 + 将要落下的节点", () =>
                {
                    int n0, n1, n2, e0, e1;
                    WorldSnapshot w = Fix.LShape(100, 10, out n0, out n1, out n2, out e0, out e1);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);

                    var anchors = new List<PlacedNode> { Fix.OnEdge(Fix.XY(0, 0), e0, 0) };
                    var cursor = Fix.OnEdge(Fix.XY(100, 100), e1, 100);
                    PreviewKit.Result r = PreviewKit.Stroke(anchors, cursor, w, cfg, t, 0.0);

                    Check.Null("P1 正常路径不带 Skip", r.Skip);
                    Check.Int("P1 一段预览折线（最后一格 → 游标）", 1, r.Segments.Count);
                    Check.Enum("P1 预览这条边真的沿网络走通了", TraceFallback.None, r.Fallback);
                    Check.True("P1 折线含两端：首点=已落档的那一格", () =>
                        Harness.Near(r.Segments[0].Points[0], anchors[0].Pos, 1e-9));
                    Check.True("P1 折线含两端：末点=将要落下的一格", () =>
                        Harness.Near(r.Segments[0].Points[r.Segments[0].Count - 1], cursor.Pos, 1e-9));
                    Check.True("P1 折线比直线密（沿 L 形走，不是斜穿）", () => r.Segments[0].Count > 2);
                    Check.True("P1 每个中间点都贴在网络上（离两条折线都不超过 1 米）", () =>
                    {
                        for (int i = 1; i < r.Segments[0].Count - 1; i++)
                        {
                            P3 p = r.Segments[0].Points[i];
                            if (Harness.DistToLine(w.Edges[e0].Line, p) > 1.0 &&
                                Harness.DistToLine(w.Edges[e1].Line, p) > 1.0) return false;
                        }
                        return true;
                    });
                    Check.Int("P1 预览节点数 = 折线中间点 + 游标自己", r.Segments[0].Count - 1, r.Dots.Count);
                    Check.True("P1 游标那一格一定在预览节点里", () =>
                    {
                        for (int i = 0; i < r.Dots.Count; i++) if (Harness.Near(r.Dots[i], cursor.Pos, 1e-9)) return true;
                        return false;
                    });
                });

                Harness.Section("P2 还没有第一格：预览只剩「你会落在这里」", () =>
                {
                    int n0, n1, n2, e0, e1;
                    WorldSnapshot w = Fix.LShape(100, 10, out n0, out n1, out n2, out e0, out e1);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);
                    var cursor = Fix.OnEdge(Fix.XY(100, 100), e1, 100);

                    PreviewKit.Result r = PreviewKit.Stroke(new List<PlacedNode>(), cursor, w, cfg, t, 0.0);
                    Check.Int("P2 空栈：没有折线", 0, r.Segments.Count);
                    Check.Int("P2 空栈：只有一个游标圈", 1, r.Dots.Count);
                    Check.True("P2 那一格就是吸附结果", () => Harness.Near(r.Dots[0], cursor.Pos, 1e-12));

                    PreviewKit.Result rNull = PreviewKit.Stroke(null, cursor, w, cfg, t, 0.0);
                    Check.Int("P2 栈为 null 不抛，同样只给一个圈", 1, rNull.Dots.Count);
                });

                Harness.Section("P3 闸门：宁可什么都不画，也不画半套", () =>
                {
                    int n0, n1, n2, e0, e1;
                    WorldSnapshot w = Fix.LShape(100, 10, out n0, out n1, out n2, out e0, out e1);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);
                    var anchors = new List<PlacedNode> { Fix.OnEdge(Fix.XY(0, 0), e0, 0) };
                    var cursor = Fix.OnEdge(Fix.XY(100, 100), e1, 100);

                    var off = ModConfig.CreateDefault();
                    off.ShowLivePreview = false;
                    PreviewKit.Result rOff = PreviewKit.Stroke(anchors, cursor, w, off, t, 0.0);
                    Check.Str("P3 预览开关关掉 ⇒ preview-off", "preview-off", rOff.Skip);
                    Check.False("P3 关掉之后一套几何都不许发布（HasContent=false）", () => rOff.HasContent);

                    var disabled = ModConfig.CreateDefault();
                    disabled.Enabled = false;
                    Check.Str("P3 总开关关掉 ⇒ mod-off", "mod-off", PreviewKit.Stroke(anchors, cursor, w, disabled, t, 0.0).Skip);

                    PreviewKit.Result rWorld = PreviewKit.Stroke(anchors, cursor, null, cfg, t, 0.0);
                    Check.Str("P3 还没有世界快照 ⇒ no-world（不拿旧几何凑一套）", "no-world", rWorld.Skip);

                    PreviewKit.Result rBudget = PreviewKit.Stroke(anchors, cursor, w, cfg, t, PreviewKit.BUDGET_MS + 1);
                    Check.Str("P3 上一帧超预算 ⇒ budget，这一帧沿用上一帧那套", "budget", rBudget.Skip);
                    Check.False("P3 超预算时不产出几何", () => rBudget.HasContent);

                    var noSnap = Fix.TraceTuning(AreaTier.District);
                    noSnap.SnapEnabled = false;
                    Check.Str("P3 这一档不吸附 ⇒ snap-off（预览没有内容可预告）", "snap-off",
                        PreviewKit.Stroke(anchors, cursor, w, cfg, noSnap, 0.0).Skip);

                    var noTrace = Fix.TraceTuning(AreaTier.District);
                    noTrace.TraceEnabled = false;
                    PreviewKit.Result rT = PreviewKit.Stroke(anchors, cursor, w, cfg, noTrace, 0.0);
                    Check.Str("P3 描边关了 ⇒ 只剩一个吸附圈，不画线", "trace-off", rT.Skip);
                    Check.Int("P3 吸附圈仍然给", 1, rT.Dots.Count);
                    Check.Int("P3 但折线一条都不许有", 0, rT.Segments.Count);
                });

                Harness.Section("P4 闭合那一帧：预览与真正落档的那次描边口径一致", () =>
                {
                    Fix.Grid g = Fix.SquareRing(100, 10, 0);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);

                    // 三个手动格 = 方形的三个角，游标回到第 0 格（= 玩家准备点闭合）。
                    var anchors = new List<PlacedNode>
                    {
                        Fix.OnEdge(Fix.XY(0, 0), g.E0, 0),
                        Fix.OnEdge(Fix.XY(100, 0), g.E0, 100),
                        Fix.OnEdge(Fix.XY(100, 100), g.E1, 100),
                    };
                    var cursor = anchors[0];
                    PreviewKit.Result r = PreviewKit.Stroke(anchors, cursor, g.World, cfg, t, 0.0);

                    // 同一头尾、同一份快照、BuildClosingRing 那一套上下文（不带游标、不报格号）。
                    TraceContext ctx = new TraceContext();
                    TraceContextUtil.Fill(anchors, ref ctx);
                    TraceResult closing = TraceKit.Trace(anchors[anchors.Count - 1], anchors[0], g.World, cfg, t, ctx);

                    Check.Enum("P4 预览给出的回退原因 == 真正落档时算出来的那一个（不许两个口径）",
                        closing.Fallback, r.Fallback);
                    Check.Int("P4 预览只有一条折线", 1, r.Segments.Count);
                    Check.True("P4 无论走通还是回退，折线都必须含两端（画出来不能缺一头）", () =>
                        r.Segments[0].Count >= 2 &&
                        Harness.Near(r.Segments[0].Points[0], anchors[anchors.Count - 1].Pos, 1e-9) &&
                        Harness.Near(r.Segments[0].Points[r.Segments[0].Count - 1], cursor.Pos, 1e-9));

                    // ⚠ 这一条钉的是**当前已知不好**的行为，不是「这样对」：
                    //   闭合这条对角线两端所贴的 E1/E0 都在弦的同一侧，「绕外圈」那两条边 E2/E3
                    //   被侧向过滤挡在搜索之外 ⇒ 只剩会与已画边重合的那条路 ⇒ 判退化 ⇒ 退回直线。
                    //   玩家视角 = 「闭合那一条边是斜的」。登记在任务 #19（下一轮修搜索的放行口径）。
                    Check.Enum("P4 现状：这种闭合形状会被判 WouldDegenerate 退回直线（任务 #19）",
                        TraceFallback.WouldDegenerate, r.Fallback);
                });

                Harness.Section("P5 预览没有资格改动任何已落档的东西", () =>
                {
                    int n0, n1, n2, e0, e1;
                    WorldSnapshot w = Fix.LShape(100, 10, out n0, out n1, out n2, out e0, out e1);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);
                    var anchors = new List<PlacedNode>
                    {
                        Fix.OnEdge(Fix.XY(0, 0), e0, 0),
                        Fix.OnEdge(Fix.XY(100, 0), e1, 0),
                        Fix.OnEdge(Fix.XY(100, 100), e1, 100),
                    };
                    var before = new List<PlacedNode>(anchors);
                    int edgePts = w.Edges[e0].Line.Count;
                    var cursor = Fix.Free(Fix.XY(50, 50));

                    PreviewKit.Stroke(anchors, cursor, w, cfg, t, 0.0);
                    PreviewKit.Stroke(anchors, cursor, w, cfg, t, 99.0);

                    Check.True("P5 手动栈一格一位都没被挪过（预览只读）", () =>
                    {
                        if (anchors.Count != before.Count) return false;
                        for (int i = 0; i < anchors.Count; i++)
                        {
                            if (!Harness.Near(anchors[i].Pos, before[i].Pos, 1e-12)) return false;
                            if (anchors[i].Kind != before[i].Kind) return false;
                            if (anchors[i].Edge != before[i].Edge) return false;
                            if (!Harness.Near(anchors[i].Arc, before[i].Arc, 1e-12)) return false;
                        }
                        return true;
                    });
                    Check.Int("P5 世界快照的折线没被塞点", edgePts, w.Edges[e0].Line.Count);
                });
            }

            // —————————————————————————— 拖拽中

            private static void DragSections()
            {
                Harness.Section("P6 拖拽预览：两段接在同一个被拖格上", () =>
                {
                    Fix.Grid g = Fix.SquareRing(100, 10, 0);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);
                    var ring = new List<P3> { Fix.XY(0, 0), Fix.XY(100, 0), Fix.XY(100, 100), Fix.XY(0, 100) };
                    var before = new List<P3>(ring);

                    // 玩家把第 1 格（右下角）沿右边往上拖到 (100,50) —— 那一格本来就在网络上。
                    P3 live = Fix.XY(100, 50);
                    PreviewKit.Result r = PreviewKit.Drag(ring, true, 1, live, g.World, cfg, t, 0.0);

                    Check.Null("P6 正常路径不带 Skip", r.Skip);
                    Check.Int("P6 左右两条边各一段", 2, r.Segments.Count);
                    Check.True("P6 两段的公共点就是被拖格（不许一个角画两处）", () =>
                    {
                        P3 a = r.Segments[0].Points[r.Segments[0].Count - 1];
                        P3 b = r.Segments[1].Points[0];
                        return Harness.Near(a, b, 1e-6);
                    });
                    Check.True("P6 被拖格出现在预览节点里", () =>
                    {
                        for (int i = 0; i < r.Dots.Count; i++) if (Harness.Near(r.Dots[i], live, 1e-6)) return true;
                        return false;
                    });
                    Check.True("P6 第一段从「左邻格原位」出发、第二段回到「右邻格原位」", () =>
                    {
                        P3 s = r.Segments[0].Points[0];
                        P3 e = r.Segments[1].Points[r.Segments[1].Count - 1];
                        return Harness.Near(s, before[0], 1e-6) && Harness.Near(e, before[2], 1e-6);
                    });
                    Check.True("P6 存档环一个字节都没动（传进去的是只读视图）", () =>
                    {
                        if (ring.Count != before.Count) return false;
                        for (int i = 0; i < ring.Count; i++) if (!Harness.Near(ring[i], before[i], 1e-12)) return false;
                        return true;
                    });

                    PreviewKit.Result gate = PreviewKit.Drag(ring, true, 1, live, g.World, cfg, t, PreviewKit.BUDGET_MS + 1);
                    Check.Str("P6 超预算 ⇒ 同一帧什么都不发（沿用上一帧预览）", "budget", gate.Skip);
                });

                Harness.Section("P7 拖拽的下标与形状不合法时安静退场", () =>
                {
                    Fix.Grid g = Fix.SquareRing(100, 10, 0);
                    var cfg = ModConfig.CreateDefault();
                    var t = Fix.TraceTuning(AreaTier.District);
                    var ring = new List<P3> { Fix.XY(0, 0), Fix.XY(100, 0), Fix.XY(100, 100), Fix.XY(0, 100) };

                    Check.Str("P7 下标越界 ⇒ bad-index", "bad-index",
                        PreviewKit.Drag(ring, true, 9, Fix.XY(50, 50), g.World, cfg, t, 0.0).Skip);
                    Check.Str("P7 下标为负（拖的不是格，是边中段）⇒ bad-index", "bad-index",
                        PreviewKit.Drag(ring, true, -1, Fix.XY(50, 50), g.World, cfg, t, 0.0).Skip);
                    Check.Str("P7 环太小（不是区域的一圈）⇒ bad-index", "bad-index",
                        PreviewKit.Drag(new List<P3> { Fix.XY(0, 0), Fix.XY(1, 0) }, true, 0, Fix.XY(2, 0), g.World, cfg, t, 0.0).Skip);
                    Check.Str("P7 没有快照 ⇒ no-world", "no-world",
                        PreviewKit.Drag(ring, true, 1, Fix.XY(50, 50), null, cfg, t, 0.0).Skip);

                    var off = ModConfig.CreateDefault();
                    off.ShowLivePreview = false;
                    Check.Str("P7 预览开关对拖拽同样有效", "preview-off",
                        PreviewKit.Drag(ring, true, 1, Fix.XY(50, 50), g.World, off, t, 0.0).Skip);
                });
            }
        }
    }
}
