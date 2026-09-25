using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// 需求 5 + 本轮反馈第 6 条「已提交的区域也要跟着路网走」的引擎层（FollowKit）。
    ///
    /// 这一节最重要的两条断言是 **Moved == 0** 和 **第二次跑逐点全等**：
    /// 自动跟随是直接写存档的功能，一旦「每次重算都挪一点点」，玩家看到的就是一整圈边界
    /// 每按一次快捷键就往某个方向爬 —— 数据坏了还查不出是哪一次坏的。
    /// 所以「本来就在路上 ⇒ 一个毫米都不许动」和「幂等」比「跟得上」更先被钉住。
    /// </summary>
    internal static partial class Tests
    {
        internal static class Follow
        {
            /// <summary>
            /// 一个 100×100 的街区，四边各一条路，路宽 12 ⇒ 内侧四条缘正好围出 88×88。
            /// <paramref name="dropBottom"/> 把「下边那条路」整体往南挪（模拟玩家改了路）。
            /// </summary>
            private static WorldSnapshot Block(double dropBottom, out Polyline[] inner)
            {
                var w = new WorldSnapshot();
                w.Nodes.Add(Fix.Node(0, 0, -dropBottom));
                w.Nodes.Add(Fix.Node(1, 100, -dropBottom));
                w.Nodes.Add(Fix.Node(2, 100, 100));
                w.Nodes.Add(Fix.Node(3, 0, 100));

                var e0 = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, -dropBottom), Fix.XY(100, -dropBottom), 10));
                e0.LeftLine = Fix.Straight(Fix.XY(0, 6 - dropBottom), Fix.XY(100, 6 - dropBottom), 10);
                e0.RightLine = Fix.Straight(Fix.XY(0, -6 - dropBottom), Fix.XY(100, -6 - dropBottom), 10);
                e0.RoadWidth = 12;

                var e1 = Fix.Edge(1, NetKind.Road, 1, 2, Fix.Straight(Fix.XY(100, -dropBottom), Fix.XY(100, 100), 10));
                e1.LeftLine = Fix.Straight(Fix.XY(94, -dropBottom), Fix.XY(94, 100), 10);
                e1.RightLine = Fix.Straight(Fix.XY(106, -dropBottom), Fix.XY(106, 100), 10);
                e1.RoadWidth = 12;

                var e2 = Fix.Edge(2, NetKind.Road, 2, 3, Fix.Straight(Fix.XY(100, 100), Fix.XY(0, 100), 10));
                e2.LeftLine = Fix.Straight(Fix.XY(100, 94), Fix.XY(0, 94), 10);
                e2.RightLine = Fix.Straight(Fix.XY(100, 106), Fix.XY(0, 106), 10);
                e2.RoadWidth = 12;

                var e3 = Fix.Edge(3, NetKind.Road, 3, 0, Fix.Straight(Fix.XY(0, 100), Fix.XY(0, -dropBottom), 10));
                e3.LeftLine = Fix.Straight(Fix.XY(-6, 100), Fix.XY(-6, -dropBottom), 10);
                e3.RightLine = Fix.Straight(Fix.XY(6, 100), Fix.XY(6, -dropBottom), 10);
                e3.RoadWidth = 12;

                w.Edges.Add(e0);
                w.Edges.Add(e1);
                w.Edges.Add(e2);
                w.Edges.Add(e3);
                w.BuildAdjacency();
                inner = new[] { e0.LeftLine, e1.LeftLine, e2.LeftLine, e3.RightLine };
                return w;
            }

            /// <summary>
            /// 内接十二格方形环（每边 3 格）：每格都正好落在某条内侧路缘上（角格落在两条线的交点，
            /// 吸附的侧向粘性会把它们判给同一条边，实测每条边都是「同边直段」）。
            /// 第六轮反馈 1 的新契约：直线边不再交付首末关节点 ⇒ 这种直段街区重描后**环原样不动**，
            /// 这是正确行为（老契约会在每段直线两端各多铺一个自动点，环越描越密）。
            /// </summary>
            private static List<P3> Square12()
            {
                return new List<P3>
                {
                    Fix.XY(94, 6), Fix.XY(94, 30), Fix.XY(94, 70), Fix.XY(94, 94),
                    Fix.XY(70, 94), Fix.XY(30, 94), Fix.XY(6, 94), Fix.XY(6, 70),
                    Fix.XY(6, 30), Fix.XY(6, 6), Fix.XY(30, 6), Fix.XY(70, 6),
                };
            }

            /// <summary>
            /// 单条拱形路：中心线 y = 12·sin(πx/100)（0..100 米、按 2.5 米采样），路宽 12，
            /// 左右缘线竖直偏移 ±6。与 Square12 的直段街区对照用：新契约砍的是**直线边**的
            /// 首末关节点，曲率超出简化容差的弯道边照旧必须补点。
            /// </summary>
            private static WorldSnapshot CurvedRoad()
            {
                var w = new WorldSnapshot();
                w.Nodes.Add(Fix.Node(0, 0, 0));
                w.Nodes.Add(Fix.Node(1, 100, 0));
                var cl = new Polyline();
                var rl = new Polyline();
                var ll = new Polyline();
                for (int i = 0; i <= 40; i++)
                {
                    double x = i * 2.5;
                    double y = 12.0 * Math.Sin(Math.PI * x / 100.0);
                    cl.Add(Fix.XY(x, y));
                    rl.Add(Fix.XY(x, y - 6));
                    ll.Add(Fix.XY(x, y + 6));
                }
                var e = Fix.Edge(0, NetKind.Road, 0, 1, cl);
                e.LeftLine = ll;
                e.RightLine = rl;
                e.RoadWidth = 12;
                w.Edges.Add(e);
                w.BuildAdjacency();
                return w;
            }

            /// <summary>逐格全等（点数 + 每格坐标）。「环不变 / 幂等」在新契约下就是这个口径。</summary>
            private static bool SamePoints(List<P3> a, List<P3> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!Harness.Near(a[i], b[i], 1e-9)) return false;
                return true;
            }

            /// <summary>
            /// 「from → mid… → to」拼成完整一段后，按 ≤2 米步长逐段采样，每个采样点都必须贴在某条路上。
            /// 新契约下 mid 可以为空（直段不补点），所以「没有斜穿街区」要按**整段弦**验，不能只数 mid。
            /// </summary>
            private static bool ChainOnRoad(WorldSnapshot w, P3 from, List<P3> mid, P3 to)
            {
                var full = new List<P3> { from };
                if (mid != null) full.AddRange(mid);
                full.Add(to);
                for (int i = 0; i + 1 < full.Count; i++)
                {
                    P3 p = full[i];
                    P3 q = full[i + 1];
                    double len = p.DistanceTo(q);
                    int steps = Math.Max(1, (int)Math.Ceiling(len / 2.0));
                    for (int k = 0; k <= steps; k++)
                    {
                        P3 s = P3.Lerp(p, q, (double)k / steps);
                        if (!OnAnyRoad(w, s, 0.05)) return false;
                    }
                }
                return true;
            }

            private static bool OnAnyRoad(WorldSnapshot w, P3 p, double tol)
            {
                for (int i = 0; i < w.Edges.Count; i++)
                {
                    GraphEdge ge = w.Edges[i];
                    if (Near(ge.Line, p, tol)) return true;
                    if (Near(ge.LeftLine, p, tol)) return true;
                    if (Near(ge.RightLine, p, tol)) return true;
                }
                return false;
            }

            private static bool Near(Polyline line, P3 p, double tol)
            {
                if (line == null || line.Count < 2) return false;
                P3 hit;
                int si;
                double st, al;
                return GeoKit.ClosestPointOnPolylineProbe(line, p, out hit, out si, out st, out al) <= tol;
            }

            /// <summary>a 的每个点到 b 折线（按闭合环对待）的最近距离都不超过 tol。</summary>
            private static bool AllOnCurve(List<P3> a, List<P3> b, double tol)
            {
                return WorstDeviation(a, b) <= tol;
            }

            /// <summary>a 的点到 b 曲线（闭合）的最大最近点距离。失败时断言里直接打出来，省一轮盲猜。</summary>
            private static double WorstDeviation(List<P3> a, List<P3> b)
            {
                if (a == null || b == null || a.Count == 0 || b.Count < 2) return double.PositiveInfinity;
                var closed = new List<P3>(b.Count + 1);
                for (int i = 0; i < b.Count; i++) closed.Add(b[i]);
                closed.Add(b[0]);
                var line = new Polyline(closed);
                double worst = 0;
                for (int i = 0; i < a.Count; i++)
                {
                    P3 hit;
                    int si;
                    double st, al;
                    double d = GeoKit.ClosestPointOnPolylineProbe(line, a[i], out hit, out si, out st, out al);
                    if (d > worst) worst = d;
                }
                return worst;
            }

            public static void Run()
            {
                var cfg = ModConfig.CreateDefault();

                Harness.Section("FollowKit 整圈重描：幂等与不漂移（需求 5 / 第 6 条）（X9）", () =>
                {
                    Polyline[] inner;
                    var w = Block(0, out inner);
                    var tuning = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    var ring = Square12();

                    // 第六轮反馈 1 之后的新契约：TraceResult.Points 只含交点/切换 pin 与曲率补点，
                    // 直线边 ⇒ 空。这个街区圈每条边都是同一条路缘上的直段 ⇒ 重描结果 == 原环，
                    // 「环不变且不被拒」就是正确行为（老契约每段直线两端各多一个自动点，环会越描越密）。
                    var r1 = FollowKit.Rebuild(ring, true, w, cfg, tuning);
                    Check.False("X9 正常街区圈不会被自检拦下", () => r1.Rejected);
                    Check.Int("X9 十二格全都认得出贴在路上", 12, CountAnchors(r1, p => p.Kind != SnapKind.Free));
                    Check.Int("X9 本来就在路上 ⇒ 一个毫米都不许挪（Moved 必须为 0）", 0, CountMoved(r1, ring));
                    Check.Int("X9 输入已经在路上 ⇒ 重描不产生任何位移", 0, r1.Moved);
                    Check.True("X9 描边沿路走：交付环上每个点都还在某条路上", () =>
                    {
                        for (int i = 0; i < r1.Ring.Count; i++) if (!OnAnyRoad(w, r1.Ring[i], 1e-6)) return false;
                        return true;
                    });
                    Check.True("X9 直段街区重描后环不变：交付环与输入环逐格相同（新契约：直线边不补点）", () =>
                        SamePoints(r1.Ring, ring));
                    Check.Int("X9 十二条边全部沿路走成功", 12, r1.Traced);
                    Check.Int("X9 一条退回直线的都没有", 0, r1.Straight);

                    // 幂等：把这一圈的结果再喂回去，**一格都不许变**。新契约下直段街区两遍的点列
                    // 完全相同，所以这里直接按逐格全等钉死（比老口径的「互为最近点距离 0」更严）。
                    // 跟随是直接写存档的功能，每次重算都挪一点就是慢慢把玩家的区域拖坏。
                    var r2 = FollowKit.Rebuild(r1.Ring, true, w, cfg, tuning);
                    Check.False("X9 第二次跑也没被拦下", () => r2.Rejected);
                    Check.Int("X9 第二次重描：输入的每一格都原地不动", 0, r2.Moved);
                    Check.True("X9 幂等（几何）：第二遍的每个点都落在第一遍的曲线上", () =>
                        AllOnCurve(r2.Ring, r1.Ring, 1e-6));
                    Check.True("X9 幂等（几何）：第一遍的每个点也都落在第二遍的曲线上", () =>
                        AllOnCurve(r1.Ring, r2.Ring, 1e-6));
                    Check.True("X9 幂等（逐格）：第二遍与第一遍点列全等", () => SamePoints(r2.Ring, r1.Ring));
                    var r3 = FollowKit.Rebuild(r2.Ring, true, w, cfg, tuning);
                    Check.Int("X9 第三遍仍然零位移（漂移是累积的，跑三遍才算看出来）", 0, r3.Moved);
                    Check.True("X9 三遍之后形状仍然不变", () => AllOnCurve(r3.Ring, r1.Ring, 1e-6) && SamePoints(r3.Ring, r1.Ring));
                    Check.True("X9 交付环始终贴路（第三遍）", () =>
                    {
                        for (int i = 0; i < r3.Ring.Count; i++) if (!OnAnyRoad(w, r3.Ring[i], 1e-6)) return false;
                        return true;
                    });
                });

                Harness.Section("FollowKit 弯道街区：直段不补点、弯道照补（第六轮反馈 1 对照）（X9a）", () =>
                {
                    // 与 X9 的直段街区对照：新契约砍掉的只是**直线边**的首末关节点，
                    // 曲率超出简化容差的弯道边仍然必须补点，否则交付环会切弦离开路面。
                    // 环 = 弯道两端的缘线端点 + 一个离路的自由格：两条弦边没有路可贴（Straight），
                    // 闭合那条「弯道端点 → 弯道端点」的边沿缘线走 ⇒ 补点只可能来自弯道边。
                    // 续接关掉：弦边的自由端是刻意离路的，不该触发「贴一段算一段」。
                    var cw = CurvedRoad();
                    var ct = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    ct.ContinuationEnabled = false;
                    var cring = new List<P3> { Fix.XY(0, -6), Fix.XY(50, -40), Fix.XY(100, -6) };
                    var c1 = FollowKit.Rebuild(cring, true, cw, cfg, ct);
                    Check.False("X9a 弯道圈不会被自检拦下", () => c1.Rejected);
                    Check.Int("X9a 弯道边沿路描出（Traced=1）", 1, c1.Traced);
                    Check.Int("X9a 两条离路的弦退回直线（Straight=2）", 2, c1.Straight);
                    Check.True("X9a 弯道边仍会补点：交付环多于输入的 3 格（弦边不补、弯道补）", () =>
                        c1.Ring.Count > cring.Count + 1);
                    Check.True("X9a 补出来的点全贴在弯道那条路缘线上（不是弦上的插值）", () =>
                    {
                        for (int i = cring.Count; i < c1.Ring.Count; i++)
                            if (!Near(cw.Edges[0].RightLine, c1.Ring[i], 1e-6)) return false;
                        return c1.Ring.Count - cring.Count >= 2;
                    });
                    var c2 = FollowKit.Rebuild(c1.Ring, true, cw, cfg, ct);
                    Check.False("X9a 第二遍不被拦下", () => c2.Rejected);
                    Check.True("X9a 幂等：弯道圈第二遍与第一遍逐格相同（补点落在缘线上 ⇒ 重描不再加点）", () =>
                        SamePoints(c2.Ring, c1.Ring));
                });

                Harness.Section("FollowKit 跟随改过的路 + 不许拖走（X9b）", () =>
                {
                    Polyline[] inner;
                    var w = Block(0, out inner);
                    var moved = Block(20, out inner);
                    var tuning = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    var ring = Square12();

                    var r = FollowKit.Rebuild(ring, true, moved, cfg, tuning);
                    Check.False("X9b 路挪了 20 米，这一圈仍可用", () => r.Rejected);
                    Check.True("X9b 南边那两格跟到了新的路缘上（y = 6-20 = -14）", () =>
                    {
                        return Math.Abs(r.Anchors[10].Pos.Y + 14) < 1e-9 && Math.Abs(r.Anchors[11].Pos.Y + 14) < 1e-9;
                    });
                    Check.True("X9b 北边那几格没有被连带挪动", () =>
                    {
                        return Math.Abs(r.Anchors[4].Pos.Y - 94) < 1e-9 && Math.Abs(r.Anchors[5].Pos.Y - 94) < 1e-9;
                    });
                    Check.InRange("X9b 挪动的格数 = 跟着动的那几格（南边 2 格，北边不该动）", r.Moved, 2, 4);
                    Check.True("X9b 跟过去之后交付环仍然贴路", () =>
                    {
                        for (int i = 0; i < r.Ring.Count; i++) if (!OnAnyRoad(moved, r.Ring[i], 1e-6)) return false;
                        return true;
                    });

                    // 规则①：超出半径的东西不追。路被整个拆掉的那一格留在原地，比跳到 200 米外另一条路上好。
                    var empty = new WorldSnapshot();
                    var input = Square12();
                    var far = FollowKit.Rebuild(input, true, empty, cfg, tuning);
                    Check.Int("X9b 路上什么都没有 ⇒ 一格都不挪", 0, far.Moved);
                    Check.True("X9b 全部退化成自由点，位置原样", () =>
                    {
                        for (int i = 0; i < far.Anchors.Count; i++)
                        {
                            if (far.Anchors[i].Kind != SnapKind.Free) return false;
                            if (far.Anchors[i].Pos.DistanceTo(input[i]) != 0) return false;
                        }
                        return true;
                    });

                    // 安全阀：细条退化圈整圈不写（写了游戏会三角化失败 ⇒ 区域隐形）
                    var sliver = new List<P3> { Fix.XY(0, 0), Fix.XY(100, 0), Fix.XY(100, 0.2), Fix.XY(0, 0.2) };
                    var bad = FollowKit.Rebuild(sliver, true, empty, cfg, tuning);
                    Check.True("X9b 退化薄片 ⇒ Rejected，调用方保持原样", () => bad.Rejected);
                    Check.Enum("X9b 拦下原因是「会退化」而不是笼统的失败", TraceFallback.WouldDegenerate, bad.RejectReason);
                    Check.Int("X9b 拦下时不交付任何点", 0, bad.Ring.Count);
                });

                Harness.Section("FollowKit 拖拽改形只看左右邻居（需求 3 的第二种模式）（X9c）", () =>
                {
                    Polyline[] inner;
                    var w = Block(0, out inner);
                    var tuning = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    var ring = Square12();

                    // 玩家把东侧下那一格从路上拽下来 8 米（往街区里）。
                    ring[1] = Fix.XY(86, 30);
                    // 第八轮反馈 3/10：玩家把那一格拽到哪儿，它就停在哪儿 —— 模组**不许**再把它吸回路缘。
                    // 老断言「x 回到 94」写的正是被撤销的行为（那句是第五轮反馈 4 的口径）。
                    // 现在模组做的事情只有一件：**走线自己**长出一个接入点（(94,30)），从那儿沿路缘走到邻居。
                    var d = FollowKit.Around(ring, true, 1, w, cfg, tuning);
                    Check.True("X9c 拖出去的那一格**不**被吸回：停在玩家松手的地方 (86,30)（反馈 3/10）", () =>
                        d.Valid && d.Dragged.Pos.DistanceTo(Fix.XY(86, 30)) == 0);
                    Check.Int("X9c 左邻居 = 前一格", 0, d.PrevIndex);
                    Check.Int("X9c 右邻居 = 后一格", 2, d.NextIndex);
                    Check.True("X9c 邻居自己也认得出在路上（不看先后点的那两格，看左右）", () =>
                        d.Prev.Kind != SnapKind.Free && d.Next.Kind != SnapKind.Free);
                    Check.True("X9c 邻居的坐标也没被挪（模组只动自动点，不动玩家点）", () =>
                        d.Prev.Pos.DistanceTo(ring[0]) == 0 && d.Next.Pos.DistanceTo(ring[2]) == 0);
                    // 新契约：接入点算自动点，可以长；但**左右两条走线不许在同一个位置各接一次**
                    //（那是零宽度的裂缝，游戏能存下却三角化不出东西 ⇒ 反馈 4「路径不显示」）。
                    Check.True("X9c 左右两条走线各接进同一个位置 ⇒ 只留一个接入点（尖刺守卫）", () =>
                        d.Despiked && d.Before.Count + d.After.Count == 1);
                    Check.True("X9c 留下来的那个接入点真的在东侧路缘上（反馈 10：自动点按规则吸附）", () =>
                        Harness.Near(d.Before.Count > 0 ? d.Before[0] : d.After[0], Fix.XY(94, 30), 1e-9));
                    // 「没有斜穿街区」在新契约下的说法：把玩家那一格的**前导线**除外，其余每一段都得贴路。
                    // 前导线本来就是他那一笔的延长，模组不许把它掰到路上去（那正是「吸附手动节点」）。
                    Check.True("X9c 除玩家那一格外，两段走线逐米采样全在路上", () =>
                        ChainOnRoad(w, d.Prev.Pos, d.Before, Fix.XY(94, 30))
                        && ChainOnRoad(w, Fix.XY(94, 30), null, d.Next.Pos));
                    Check.True("X9c 两段各自产出的点（若有）也都贴路", () =>
                    {
                        for (int i = 0; i < d.Before.Count; i++) if (!OnAnyRoad(w, d.Before[i], 1e-6)) return false;
                        for (int i = 0; i < d.After.Count; i++) if (!OnAnyRoad(w, d.After[i], 1e-6)) return false;
                        return true;
                    });
                    Check.True("X9c 拖拽改形不会把不相邻的点也拖走：邻居位置原样", () =>
                        d.Prev.Pos.DistanceTo(ring[d.PrevIndex]) == 0 && d.Next.Pos.DistanceTo(ring[d.NextIndex]) == 0);

                    var wrap = FollowKit.Around(Square12(), true, 0, w, cfg, tuning);
                    Check.Int("X9c 闭合环的第 0 格 ⇒ 左邻居绕到最后一格", 11, wrap.PrevIndex);
                    Check.Int("X9c 闭合环的第 0 格 ⇒ 右邻居是第 1 格", 1, wrap.NextIndex);

                    var open = FollowKit.Around(Square12(), false, 0, w, cfg, tuning);
                    Check.Int("X9c 还没闭合 ⇒ 第 0 格左边没有邻居，不能绕回末尾", -1, open.PrevIndex);
                    Check.Int("X9c 还没闭合 ⇒ 右边照常", 1, open.NextIndex);
                    var last = FollowKit.Around(Square12(), false, 11, w, cfg, tuning);
                    Check.Int("X9c 还没闭合 ⇒ 最后一格只有左邻居", 10, last.PrevIndex);
                    Check.Int("X9c 还没闭合 ⇒ 最后一格右边没有邻居", -1, last.NextIndex);
                    Check.True("X9c 下标越界 ⇒ 结果标记为不可用，而不是抛异常（拖拽目标可能已经被游戏删了）", () =>
                    {
                        var bad = FollowKit.Around(Square12(), true, 99, w, cfg, tuning);
                        return !bad.Valid && bad.Before.Count == 0 && bad.After.Count == 0;
                    });
                });

                Harness.Section("拖拽落档的局部拼接：只换左右两条边，其余一格都不许动（第六轮反馈 6）（X9e）", () =>
                {
                    // 这一节钉的是**写盘那一侧**的口径。预览走 Around，落档也必须走 Around +
                    // SpliceLocal，两边看到的形状才一致（反馈 6：「能看到预览路径和节点，
                    // 但放置后还是在原位」）。三条最要紧的判据：
                    //  ① 没被碰的格子坐标**逐个全等**（不是"差不多在路上"）—— 那是玩家的存档；
                    //  ② 自检用的局部链就是写进环里的那一段（否则闸与产物各说各话）；
                    //  ③ 认不出被拖格 ⇒ 交回空表，调用方保持原样，绝不猜一格去写。
                    Polyline[] inner;
                    var w = Block(0, out inner);
                    var tuning = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    var ring = Square12();
                    ring[1] = Fix.XY(86, 30);        // 玩家把东侧那一格往街区里拽了 8 米

                    var d = FollowKit.Around(ring, true, 1, w, cfg, tuning);
                    var nr = FollowKit.SpliceLocal(ring, d);
                    var chain = FollowKit.LocalChain(ring, d);
                    int draggedAt = 1 + d.Before.Count;
                    int nextAt = 2 + d.Before.Count + d.After.Count;

                    Check.True("X9e 前置：被拖的那一格认得出左右邻居（拼接测的是「两段怎么走、怎么装回环里」）",
                        () => d.Valid && d.PrevIndex == 0 && d.NextIndex == 2);
                    Check.Int("X9e 局部链 = 左邻 + 左边点 + 被拖格 + 右边点 + 右邻",
                        3 + d.Before.Count + d.After.Count, chain.Count);
                    Check.Int("X9e 新环点数 = 原环 + 两段重描点", ring.Count + d.Before.Count + d.After.Count, nr.Count);
                    // 第八轮反馈 1（直线上不该多出节点）与 3/10（手动节点不许被吸附）合起来的新契约：
                    // 直线段上模组长出来的**只有**那一个接入点，而且左右两条走线共用它 ——
                    // 多一个就是零宽度裂缝（X9c 的尖刺守卫），一个都没有则等于没贴合（反馈 2/8）。
                    Check.Int("X9e 直段街区拖一格 ⇒ 只补一个接入点（不多不少）", 1, d.Before.Count + d.After.Count);
                    Check.True("X9e 自检看的那段链就是写进环里的前缀（闸与产物同一份几何）", () =>
                    {
                        if (chain.Count == 0 || chain.Count > nr.Count) return false;
                        for (int i = 0; i < chain.Count; i++) if (!Harness.Near(chain[i], nr[i], 1e-12)) return false;
                        return true;
                    });
                    Check.True("X9e 左右邻居写回的是它们**原来**的坐标（反馈 3：手动节点不许被模组吸附）", () =>
                        nr[0].DistanceTo(ring[d.PrevIndex]) == 0 && nr[nextAt].DistanceTo(ring[d.NextIndex]) == 0);
                    Check.True("X9e 被拖那一格写进去的就是玩家松手那一个坐标（反馈 3/10 的硬判据）", () =>
                        nr[draggedAt].DistanceTo(d.Dragged.Pos) == 0 && nr[draggedAt].DistanceTo(ring[1]) == 0);
                    Check.True("X9e 其余每一格原样保留：顺序与坐标都不变（「没碰的地方不许改存档」的硬判据）", () =>
                    {
                        for (int i = 1; i <= ring.Count - 3; i++)
                        {
                            P3 want = ring[(d.NextIndex + i) % ring.Count];
                            if (nr[nextAt + i].DistanceTo(want) != 0) return false;
                        }
                        return true;
                    });

                    // 弯道那一侧：补点是允许的，但补出来的点必须全在路上，且没被碰的格子仍旧一个不动。
                    var cw = CurvedRoad();
                    var ct = Fix.Tun(AreaTier.Lot, 30, 0.5, 60, 2.0, 3.0);
                    ct.ContinuationEnabled = false;
                    var cring = new List<P3>();
                    for (int i = 0; i < 4; i++)
                    {
                        double x = i * 100.0 / 3.0;
                        cring.Add(Fix.XY(x, 12.0 * Math.Sin(Math.PI * x / 100.0) - 6.0));
                    }
                    var off = new List<P3>(cring);
                    off[1] = Fix.XY(cring[1].X, cring[1].Y - 4);      // 往路外拽 4 米（仍在吸附半径内）
                    var cd = FollowKit.Around(off, true, 1, cw, cfg, ct);
                    var cnr = FollowKit.SpliceLocal(off, cd);
                    Check.True("X9e 弯道拖一格 ⇒ 拼接成功", () => cd.Valid && cnr.Count >= off.Count);
                    // 补出来的**自动**点必须全在缘线上；被拖那一格本来就是玩家放在路外的，
                    // 它不参与这条判据（第八轮之后它不许被吸回路缘，见 X9c）。
                    Check.True("X9e 弯道补出来的自动点全贴在这条路的缘线上", () =>
                    {
                        for (int i = 0; i < cd.Before.Count; i++) if (!OnAnyRoad(cw, cd.Before[i], 1e-6)) return false;
                        for (int i = 0; i < cd.After.Count; i++) if (!OnAnyRoad(cw, cd.After[i], 1e-6)) return false;
                        return true;
                    });
                    Check.True("X9e 弯道上被拖那一格仍旧停在玩家放的位置", () =>
                        cnr[1 + cd.Before.Count].DistanceTo(off[1]) == 0);
                    Check.True("X9e 弯道上没被碰的那一格也照样原样保留", () =>
                    {
                        int cNext = 2 + cd.Before.Count + cd.After.Count;
                        for (int i = 1; i <= off.Count - 3; i++)
                            if (cnr[cNext + i].DistanceTo(off[(cd.NextIndex + i) % off.Count]) != 0) return false;
                        return true;
                    });

                    Check.True("X9e 认不出被拖的是哪一格 ⇒ 交回空表（调用方保持原样，绝不猜一格去写）", () =>
                    {
                        var bad = FollowKit.Around(Square12(), true, 99, w, cfg, tuning);
                        return FollowKit.SpliceLocal(Square12(), bad).Count == 0
                            && FollowKit.LocalChain(Square12(), bad).Count == 0;
                    });
                    Check.True("X9e 环太短（两格围不成区域）⇒ 空表，不抛异常", () =>
                    {
                        var two = new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0) };
                        var dd = FollowKit.Around(two, true, 0, w, cfg, tuning);
                        return FollowKit.SpliceLocal(two, dd).Count == 0;
                    });
                    Check.True("X9e null 输入 ⇒ 空表（拖拽目标可能已经被游戏删掉）", () =>
                        FollowKit.SpliceLocal(null, d).Count == 0 && FollowKit.LocalChain(ring, null).Count == 0);
                });

                Harness.Section("GeoKit 形状相同判定：跟随不重写、点数不增长的地基（X10）", () =>
                {
                    var square = new List<P3> { Fix.XY(0, 0), Fix.XY(100, 0), Fix.XY(100, 100), Fix.XY(0, 100) };
                    var dense = new List<P3>();
                    for (int i = 0; i < 40; i++) dense.Add(Fix.XY(i * 2.5, 0));
                    for (int i = 0; i < 40; i++) dense.Add(Fix.XY(100, i * 2.5));
                    for (int i = 0; i < 40; i++) dense.Add(Fix.XY(100 - i * 2.5, 100));
                    for (int i = 0; i < 40; i++) dense.Add(Fix.XY(0, 100 - i * 2.5));

                    Check.True("X10 同一个正方形，4 个点与 160 个点算「一样」⇒ 跟随不会白白重写存档",
                        () => GeoKit.CurvesAgree(square, true, dense, true, 0.05));
                    Check.True("X10 反过来也一样（判定是对称的，不是只查密的那一侧）",
                        () => GeoKit.CurvesAgree(dense, true, square, true, 0.05));
                    Check.True("X10 末点重复首点的写法与环形索引写法算「一样」⇒ 游戏两种环都不会被误判成改动",
                        () =>
                        {
                            var dup = new List<P3>(square);
                            dup.Add(square[0]);
                            return GeoKit.CurvesAgree(square, true, dup, false, 0.05);
                        });

                    var shifted = new List<P3> { Fix.XY(0, 1), Fix.XY(100, 1), Fix.XY(100, 101), Fix.XY(0, 101) };
                    Check.False("X10 整体挪 1 米 ⇒ 判成不一样（真的改动必须写）",
                        () => GeoKit.CurvesAgree(square, true, shifted, true, 0.05));
                    var notched = new List<P3>(square);
                    notched.Add(Fix.XY(50, 60));            // 多一个鼓出去 60 米的角
                    Check.False("X10 多一个鼓包 ⇒ 判成不一样", () => GeoKit.CurvesAgree(square, true, notched, true, 0.05));

                    Check.Close("X10 单向最大偏离：点离曲线 2 米就是 2 米",
                        2.0, GeoKit.MaxDeviationToCurve(new List<P3> { Fix.XY(50, 2) }, square, true), 1e-9);
                    Check.Close("X10 曲线上的点偏离 0", 0, GeoKit.MaxDeviationToCurve(new List<P3> { Fix.XY(100, 40) }, square, true), 1e-9);

                    var boxClosed = GeoKit.CurveBounds(square, true);
                    var boxOpen = GeoKit.CurveBounds(square, false);
                    Check.True("X10 闭合与否不改变包围盒（收尾弦两端本来就在点集里）⇒ 这个参数是口径声明，不是几何差",
                        () => boxClosed.MinX == boxOpen.MinX && boxClosed.MinY == boxOpen.MinY
                            && boxClosed.MaxX == boxOpen.MaxX && boxClosed.MaxY == boxOpen.MaxY);
                    Check.True("X10 空集合给的是「无效框」而不是 0,0,0,0 那种看起来像原点的假盒子",
                        () => !GeoKit.CurveBounds(new List<P3>(), true).IsValid);
                });

                Harness.Section("FollowKit 质心用面积心而不是顶点平均（X9d）", () =>
                {
                    // 一条长边上铺了一串密集描边点、对边只有两个端点 —— 跟随场景的环天生就是这个形状
                    // （我们写进存档的自动点本来就密）。顶点平均会被点多的那边拽过去，侧向选择跟着翻。
                    var lopsided = new List<P3>
                    {
                        Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(10, 10), Fix.XY(0, 10),
                    };
                    for (int i = 1; i < 10; i++) lopsided.Add(Fix.XY(0, i));     // x=0 那条边上补 9 个点
                    var areaCentroid = FollowKit.Centroid(lopsided);
                    Check.Close("X9d 面积心：矩形就是几何中心 (5,5)", 5, areaCentroid.X, 1e-9);
                    Check.Close("X9d 面积心：y 同样是 5", 5, areaCentroid.Y, 1e-9);
                    double vx = 0, vy = 0;
                    for (int i = 0; i < lopsided.Count; i++) { vx += lopsided[i].X; vy += lopsided[i].Y; }
                    vx /= lopsided.Count;
                    vy /= lopsided.Count;
                    Check.True("X9d 顶点平均确实会偏（偏了才说明这条断言不是在原地打转）", () =>
                        Math.Abs(vx - 5) > 0.5 && Math.Abs(areaCentroid.X - vx) > 0.4);

                    var line = new List<P3> { Fix.XY(0, 0), Fix.XY(10, 0), Fix.XY(20, 0) };
                    var back = FollowKit.Centroid(line);
                    Check.True("X9d 退化成一条线时退回顶点平均而不是 NaN/0", () =>
                        Math.Abs(back.X - 10) < 1e-9 && Math.Abs(back.Y) < 1e-9);
                });
            }

            private static int CountAnchors(FollowKit.FollowResult r, Func<PlacedNode, bool> pred)
            {
                int c = 0;
                for (int i = 0; i < r.Anchors.Count; i++) if (pred(r.Anchors[i])) c++;
                return c;
            }

            /// <summary>反推锚点相对输入位置真的动了的格数（不依赖结果自己记的 Moved，独立量一遍）。</summary>
            private static int CountMoved(FollowKit.FollowResult r, IList<P3> input)
            {
                int c = 0;
                for (int i = 0; i < r.Anchors.Count && i < input.Count; i++)
                {
                    if (r.Anchors[i].Pos.DistanceTo(input[i]) > 1e-9) c++;
                }
                return c;
            }
        }
    }
}
