using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    /// <summary>
    /// X11：AnchorRebind —— 手动栈跨帧持有，而世界快照每次重抓都是一份新的下标表。
    ///
    /// 【这一层为什么单独有断言】v0.1.2 第三轮实机的账是：snap=2907（游标确实会吸）却
    /// traceHit=17 / fallback=119（边几乎全退回直线），玩家视角=「完全不会自动贴合」。
    /// 根因不在算法，在**锚点记的是下标**：EnsureWorld 一长大就重抓快照 ⇒ 老锚点的 Edge 号
    /// 要么越界、要么指向别条路 ⇒ TraceKit 直接 NotOnNetwork。离线夹具以前每次都现造快照，
    /// 从来没在「同一局里换过一份快照」上跑过描边，所以这条雷整轮没被任何断言拦住。
    ///
    /// 口径：重绑改的是「挂在哪条线的哪个弧长」，**位置一格都不许动**（那是玩家点下的几何）。
    /// 每个分支都单独钉这条。
    /// </summary>
    internal static partial class Tests
    {
        internal static class Rebind
        {
            // 夹具：一条 0→(100,0) 的主路（签名 7000）+ 一条远处的干扰路（签名 7001）。
            // 两个版本只是数组顺序不同 —— 这就是「同一条路，两次抓取给了不同下标」。
            private static WorldSnapshot Roads(string order)
            {
                WorldSnapshot w = new WorldSnapshot();
                if (order == "main-first")
                {
                    w.Nodes.Add(Node(0, 0, 0));
                    w.Nodes.Add(Node(1, 100, 0));
                    w.Nodes.Add(Node(2, 500, 500));
                    w.Nodes.Add(Node(3, 500, 560));
                    w.Edges.Add(Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10)));
                    w.Edges.Add(Edge(1, NetKind.Road, 2, 3, Fix.Straight(Fix.XY(500, 500), Fix.XY(500, 560), 6)));
                }
                else
                {
                    // 干扰路排在前面：主路从 0 号变成 1 号，两个图节点的下标也整体后移。
                    w.Nodes.Add(Node(2, 500, 500));
                    w.Nodes.Add(Node(3, 500, 560));
                    w.Nodes.Add(Node(0, 0, 0));
                    w.Nodes.Add(Node(1, 100, 0));
                    w.Edges.Add(Edge(1, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(500, 500), Fix.XY(500, 560), 6)));
                    w.Edges.Add(Edge(0, NetKind.Road, 2, 3, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 10)));
                }
                w.BuildAdjacency();
                return w;
            }

            private static GraphNode Node(int id, double x, double y)
            {
                return new GraphNode { Id = id, Pos = Fix.XY(x, y), Signature = 1000 + id };
            }

            private static GraphEdge Edge(int id, NetKind kind, int startNode, int endNode, Polyline line)
            {
                return new GraphEdge
                {
                    Id = id,
                    Kind = kind,
                    StartNode = startNode,
                    EndNode = endNode,
                    Line = line,
                    Length = line == null ? 0 : line.Length2D(),
                    Signature = 7000 + id
                };
            }

            private static PlacedNode Centre(P3 pos, int edge, double arc, long sig)
            {
                return new PlacedNode(pos, SnapKind.NetCentre, -1, edge, arc, sig);
            }

            public static void Run()
            {
                var cfg = ModConfig.CreateDefault();
                var tuning = Fix.TraceTuning(AreaTier.District);

                Harness.Section("AnchorRebind：重抓快照后锚点必须搬回新下标（X11，第三轮实机根因）", () =>
                {
                    WorldSnapshot a = Roads("main-first");
                    WorldSnapshot b = Roads("decoy-first");

                    // 玩家是在 a 这份快照上点的两格：都记成 0 号边（主路）。
                    PlacedNode n0 = Centre(Fix.XY(20, 0), 0, 20, a.Edges[0].Signature);
                    PlacedNode n1 = Centre(Fix.XY(80, 0), 0, 80, a.Edges[0].Signature);

                    // 先把「身份对不上」钉住：拿旧下标在新快照里查，0 号已经是那条 500 m 外的干扰路。
                    Check.True("X11 不重绑：旧 0 号边在新快照里已经是别的路（签名对不上）", () =>
                        b.Edges[n0.Edge].Signature != n0.Signature);
                    // 第八轮的接入点模型顺带把这一类脏输入兜住了（StuckOnTarget）：
                    // 锚点报的边号与它自己的坐标差了 480 米 ⇒ 这个端点**不算**已经贴在网络上 ⇒
                    // 走线自己在玩家那一格旁边重挂一个接入点，挂到的正是真主路（1 号）。
                    // 这不是「重绑可以不要」的理由 —— 边号/签名要是错的，缓存与复用的判据就全乱了，
                    // 所以下面 AnchorRebind 那一串的断言仍旧一条不能少。
                    Check.True("X11 不重绑：走线自己纠正到真正压着的那条路（第八轮 StuckOnTarget 的兜底）", () =>
                    {
                        TraceResult bad = TraceKit.Trace(n0, n1, b, cfg, tuning, Fix.EmptyCtx());
                        return bad.UsedNetwork && bad.UsedEdges.Count > 0 && bad.UsedEdges[0] == 1;
                    });
                    // 同一条兜底只在「吸附还开着」时存在：把吸附关掉（接入点没有输入）⇒ 老缺陷立刻回来。
                    // 这一条是这两层保险各管一段的凭证：几何归接入点判据，身份归重绑。
                    Check.True("X11 关掉吸附 ⇒ 走线不再自己纠正（说明重绑这一层始终是必需的）", () =>
                    {
                        var noSnap = Fix.TraceTuning(AreaTier.District);
                        noSnap.SnapEnabled = false;
                        TraceResult bad = TraceKit.Trace(n0, n1, b, cfg, noSnap, Fix.EmptyCtx());
                        return !bad.UsedNetwork || bad.UsedEdges.Count == 0 || bad.UsedEdges[0] != 1;
                    });

                    AnchorRebind.Stats st = new AnchorRebind.Stats();
                    PlacedNode r0 = AnchorRebind.Node(n0, b, cfg, tuning, st);
                    PlacedNode r1 = AnchorRebind.Node(n1, b, cfg, tuning, st);

                    Check.Int("X11 主路边号搬回 1（重抓后它是 1 号）", 1, r0.Edge);
                    Check.Int("X11 第二格同样搬回 1", 1, r1.Edge);
                    Check.Int("X11 账：搬回下标 2 次", 2, st.Rebound);
                    Check.Int("X11 账：不需要换认目标", 0, st.Resnapped);
                    Check.True("X11 位置一个毫米都不动（第一格）", () => r0.Pos.DistanceTo(n0.Pos) <= GeoKit.EPS);
                    Check.True("X11 位置一个毫米都不动（第二格）", () => r1.Pos.DistanceTo(n1.Pos) <= GeoKit.EPS);
                    Check.Close("X11 弧长按新快照重算：仍然贴着主路（20 米）", 20, r0.Arc, 1e-6);

                    TraceResult ok = TraceKit.Trace(r0, r1, b, cfg, tuning, Fix.EmptyCtx());
                    Check.Bool("X11 重绑之后描边恢复：用上了网络", true, ok.UsedNetwork);
                    Check.Enum("X11 重绑之后没有回退", TraceFallback.None, ok.Fallback);
                    // 第六轮新契约：直线边不再吐端点旁的自动点 ⇒ 这里 Points 可以为空；
                    // 「没跟着干扰路跑」由 UsedEdges 与「若有补点则必在主路上」两条共同钉住。
                    Check.True("X11 描出来的点（若有）全在主路上（没跟着干扰路跑）", () =>
                    {
                        for (int i = 0; i < ok.Points.Count; i++)
                        {
                            P3 p = ok.Points[i];
                            if (Math.Abs(p.Y) > 1e-6 || p.X < 0 || p.X > 100) return false;
                        }
                        return true;
                    });
                    Check.True("X11 用的就是重绑后那条边", () => ok.UsedEdges.Count == 1 && ok.UsedEdges[0] == 1);
                });

                Harness.Section("AnchorRebind：认不出来时的两种下场 + 幂等（X11b）", () =>
                {
                    WorldSnapshot a = Roads("main-first");
                    PlacedNode onMain = Centre(Fix.XY(20, 0), 0, 20, a.Edges[0].Signature);

                    // 主路被拆了：只剩那条远处的干扰路 ⇒ 20 米内没有东西可认 ⇒ 退回自由点，位置原样。
                    WorldSnapshot demolished = new WorldSnapshot();
                    demolished.Nodes.Add(Node(2, 500, 500));
                    demolished.Nodes.Add(Node(3, 500, 560));
                    demolished.Edges.Add(Edge(1, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(500, 500), Fix.XY(500, 560), 6)));
                    demolished.BuildAdjacency();
                    AnchorRebind.Stats st1 = new AnchorRebind.Stats();
                    PlacedNode lost = AnchorRebind.Node(onMain, demolished, cfg, tuning, st1);
                    Check.Enum("X11b 路没了 ⇒ 退回自由点（不许跳到 500 米外那条路）", SnapKind.Free, lost.Kind);
                    Check.True("X11b 退回自由点时位置照旧", () => lost.Pos.DistanceTo(onMain.Pos) <= GeoKit.EPS);
                    Check.Int("X11b 账：认不出记 Lost", 1, st1.Lost);

                    // 主路没了、但一条新路就在旁边（同一条走廊里 y=0 → y=4）⇒ 按位置重认，位置仍不动。
                    WorldSnapshot rebuilt = new WorldSnapshot();
                    rebuilt.Nodes.Add(Node(9, 0, 4));
                    rebuilt.Nodes.Add(Node(10, 100, 4));
                    rebuilt.Edges.Add(Edge(9, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 4), Fix.XY(100, 4), 10)));
                    rebuilt.BuildAdjacency();
                    AnchorRebind.Stats st2 = new AnchorRebind.Stats();
                    PlacedNode resnapped = AnchorRebind.Node(onMain, rebuilt, cfg, tuning, st2);
                    Check.Int("X11b 换认目标记 Resnapped", 1, st2.Resnapped);
                    Check.Enum("X11b 重认到的是一条中心线锚点", SnapKind.NetCentre, resnapped.Kind);
                    Check.True("X11b 重认到新路上了，但位置仍是玩家点的那一格（y=0）", () =>
                        Math.Abs(resnapped.Pos.Y) <= GeoKit.EPS && Math.Abs(resnapped.Pos.X - 20) <= GeoKit.EPS);
                    Check.Close("X11b 弧长按新线重新量过（≈20）", 20, resnapped.Arc, 1e-6);

                    // 幂等：同一份快照上再来一次，账不许再涨（跟随重描那条路每帧都可能重绑，账乱涨就是没收敛）。
                    int before = st2.Rebound + st2.Resnapped + st2.Lost;
                    AnchorRebind.Stats st3 = new AnchorRebind.Stats();
                    PlacedNode again = AnchorRebind.Node(resnapped, rebuilt, cfg, tuning, st3);
                    Check.Int("X11b 第二次重绑：账不再涨", 0, st3.Rebound + st3.Resnapped + st3.Lost);
                    Check.Int("X11b 第二次重绑：边号不变", resnapped.Edge, again.Edge);
                    Check.True("X11b 第二次重绑：结果与第一次逐字段相同", () =>
                        again.Kind == resnapped.Kind && again.Edge == resnapped.Edge && again.Side == resnapped.Side
                        && Math.Abs(again.Arc - resnapped.Arc) <= 1e-9 && again.Pos.DistanceTo(resnapped.Pos) <= GeoKit.EPS);
                    Check.True("X11b 上面那次「换认目标」的账没有被第二次重复计", () =>
                        st2.Rebound + st2.Resnapped + st2.Lost == before);
                });

                Harness.Section("AnchorRebind：交叉口/边界/建筑锚点也要跟着搬（X11c）", () =>
                {
                    WorldSnapshot a = Roads("main-first");
                    WorldSnapshot b = Roads("decoy-first");

                    // 交叉口锚点：签名记的是图节点，重绑后要按新节点号重挑所属边与弧长。
                    PlacedNode junction = new PlacedNode(Fix.XY(0, 0), SnapKind.NetNode, 0, 0, 0, a.Nodes[0].Signature);
                    PlacedNode jr = AnchorRebind.Node(junction, b, cfg, tuning, null);
                    Check.Int("X11c 交叉口节点号搬到新下标", 2, jr.GraphNode);
                    Check.Int("X11c 所属边跟着搬到主路", 1, jr.Edge);
                    Check.Close("X11c 交叉口弧长仍是「这条边的这一头」（0 或全长）", 0, jr.Arc, 1e-9);
                    Check.True("X11c 交叉口位置不动", () => jr.Pos.DistanceTo(junction.Pos) <= GeoKit.EPS);

                    // 已有区域边界锚点。
                    WorldSnapshot withBorder = new WorldSnapshot();
                    withBorder.Borders.Add(Fix.Border(5, AreaTier.District, Fix.Straight(Fix.XY(-10, -10), Fix.XY(-10, 40), 5)));
                    withBorder.Borders.Add(Fix.Border(6, AreaTier.District, Fix.Straight(Fix.XY(0, 10), Fix.XY(100, 10), 10)));
                    PlacedNode borderAnchor = new PlacedNode(Fix.XY(0, 10), SnapKind.AreaBorderSame, -1,
                        AnchorCode.Border(1), 0, withBorder.Borders[1].Signature);
                    PlacedNode moved = AnchorRebind.Node(borderAnchor, ReorderBorders(withBorder), cfg, tuning, null);
                    Check.Int("X11c 边界锚点搬到新的边界下标", AnchorCode.Border(0), moved.Edge);
                    Check.True("X11c 边界锚点位置不动", () => moved.Pos.DistanceTo(borderAnchor.Pos) <= GeoKit.EPS);
                });

                Harness.Section("AnchorRebind：接管/自愈补的锚点（Attribute）+ 整栈重绑（X11d）", () =>
                {
                    WorldSnapshot b = Roads("decoy-first");

                    // 接管与自愈那条路：位置照单收下，但锚点必须认出来 ——
                    // 老实现塞 SnapKind.Free，于是那一圈每条边都判 NotOnNetwork（第三轮实机 adopted 之后 traceHit 归零）。
                    PlacedNode adoptedFree = AnchorRebind.Attribute(Fix.Free(Fix.XY(40, 0)), b, cfg, tuning);
                    Check.Enum("X11d 接管的点在路边上 ⇒ 认出中心线锚点", SnapKind.NetCentre, adoptedFree.Kind);
                    Check.Int("X11d 认到的边号是新快照里的主路", 1, adoptedFree.Edge);
                    Check.True("X11d 认锚点不改位置（那是玩家存好的几何）", () =>
                        adoptedFree.Pos.DistanceTo(Fix.XY(40, 0)) <= GeoKit.EPS);

                    PlacedNode nowhere = AnchorRebind.Attribute(Fix.Free(Fix.XY(300, 300)), b, cfg, tuning);
                    Check.Enum("X11d 半径内什么都没有 ⇒ 保持自由点", SnapKind.Free, nowhere.Kind);
                    Check.True("X11d 保持自由点时位置照旧", () => nowhere.Pos.DistanceTo(Fix.XY(300, 300)) <= GeoKit.EPS);

                    // 整栈：混合锚点重绑后，每一格位置都不许动，且重绑后相邻两格能描出边。
                    ManualStack stack = new ManualStack();
                    stack.Push(Centre(Fix.XY(20, 0), 0, 20, b.Edges[1].Signature));
                    stack.Push(Centre(Fix.XY(60, 0), 0, 60, b.Edges[1].Signature));
                    stack.Push(Fix.Free(Fix.XY(80, 0)));
                    List<P3> before = new List<P3>();
                    for (int i = 0; i < stack.Count; i++) before.Add(stack.Nodes[i].Pos);

                    AnchorRebind.Stats st = new AnchorRebind.Stats();
                    AnchorRebind.Stack(stack, b, cfg, tuning, st);
                    Check.Int("X11d 整栈重绑：逐格都记账", 3, st.Total);
                    Check.True("X11d 整栈重绑：每一格位置都不动", () =>
                    {
                        for (int i = 0; i < stack.Count; i++) if (stack.Nodes[i].Pos.DistanceTo(before[i]) > GeoKit.EPS) return false;
                        return true;
                    });
                    Check.True("X11d 重绑之后这一圈的第一条边能沿主路描出来", () =>
                    {
                        TraceResult r = TraceKit.Trace(stack.Nodes[0], stack.Nodes[1], b, cfg, tuning, Fix.EmptyCtx());
                        return r.UsedNetwork;
                    });
                    Check.Enum("X11d 整栈重绑顺带把那个自由点认成锚点（自愈/接管同一条口径）", SnapKind.NetCentre, stack.Nodes[2].Kind);
                    Check.True("X11d 认成锚点也不许挪位置", () => stack.Nodes[2].Pos.DistanceTo(Fix.XY(80, 0)) <= GeoKit.EPS);
                });

                Harness.Section("PolicyKit.PlanStroke：走廊单调扩张，必须一直含住每个已放下的角（X11e，第四轮实机）", () =>
                {
                    var anchors = new List<P3>();
                    double radius = 96.0;

                    // 第一笔：从 (0,0) 点到 (0,100)。
                    anchors.Add(Fix.XY(0, 0));
                    PolicyKit.StrokePlan p1 = PolicyKit.PlanStroke(anchors, Fix.XY(0, 0), Fix.XY(0, 100), radius, Box2.Empty());
                    Check.True("X11e 第一笔的框含住两端", () => p1.Capture.Contains(Fix.XY(0, 0)) && p1.Capture.Contains(Fix.XY(0, 100)));
                    Check.Bool("X11e 第一笔没撞上限", false, p1.Clamped);

                    // 玩家往远处的拐角点一下：老写法走廊整个跳过去 ⇒ 早先那条路被丢出快照 ⇒ NotOnNetwork。
                    anchors.Add(Fix.XY(0, 100));
                    PolicyKit.StrokePlan p2 = PolicyKit.PlanStroke(anchors, Fix.XY(0, 100), Fix.XY(900, 100), radius, p1.Raw);
                    Check.True("X11e 点到 900 米外之后，框仍然含住最早那个角（这就是「有的路不贴」的那条路）",
                        () => p2.Capture.Contains(Fix.XY(0, 0)));
                    Check.True("X11e 也含住本段两端", () => p2.Capture.Contains(Fix.XY(0, 100)) && p2.Capture.Contains(Fix.XY(900, 100)));
                    Check.True("X11e 框只长不缩（左边界不许往右跑）", () => p2.Raw.MinX <= p1.Raw.MinX + GeoKit.EPS
                        && p2.Raw.MaxX >= p1.Raw.MaxX - GeoKit.EPS
                        && p2.Raw.MinY <= p1.Raw.MinY + GeoKit.EPS
                        && p2.Raw.MaxY >= p1.Raw.MaxY - GeoKit.EPS);
                    Check.True("X11e 抓取框也只长不缩", () => p2.Capture.MinX <= p1.Capture.MinX + GeoKit.EPS
                        && p2.Capture.MaxX >= p1.Capture.MaxX - GeoKit.EPS);

                    // 再点回来：仍然含住全部三个角（老写法在这里会第二次把快照换小）。
                    anchors.Add(Fix.XY(900, 100));
                    PolicyKit.StrokePlan p3 = PolicyKit.PlanStroke(anchors, Fix.XY(900, 100), Fix.XY(10, 20), radius, p2.Raw);
                    Check.True("X11e 回头点也在同一张走廊里", () =>
                    {
                        for (int i = 0; i < anchors.Count; i++) if (!p3.Capture.Contains(anchors[i])) return false;
                        return p3.Capture.Contains(Fix.XY(10, 20));
                    });
                    Check.True("X11e 外扩至少一个半径（锚点两侧的路都要在框里）", () =>
                        p3.Capture.Contains(Fix.XY(900 + radius * 0.9, 100)));

                    // 尺寸上限：宁可少贴 + 一行可判读的日志，也不每帧扫半张城。
                    var huge = new List<P3> { Fix.XY(0, 0), Fix.XY(9000, 9000) };
                    PolicyKit.StrokePlan big = PolicyKit.PlanStroke(huge, Fix.XY(0, 0), Fix.XY(9000, 9000), radius, Box2.Empty());
                    Check.Bool("X11e 超出上限要说出来（调用方据此打一行 Warn）", true, big.Clamped);
                    Check.True("X11e 上限是真的硬闸：不许比 STROKE_LIMIT 宽", () => big.Capture.MaxSide <= PolicyKit.STROKE_LIMIT + 1e-6);

                    // 空锚点 / null 都要能活（第一笔与切工具后的干净状态）。
                    PolicyKit.StrokePlan solo = PolicyKit.PlanStroke(null, Fix.XY(5, 5), Fix.XY(6, 6), radius, Box2.Empty());
                    Check.True("X11e 没有锚点时也能出一张框", () => solo.Capture.Contains(Fix.XY(5, 5)));
                });
            }

            /// <summary>把边界表倒过来（同一条边界换了号），用来验证签名重绑而不是靠下标撞对。</summary>
            private static WorldSnapshot ReorderBorders(WorldSnapshot src)
            {
                WorldSnapshot w = new WorldSnapshot();
                for (int i = src.Borders.Count - 1; i >= 0; i--) w.Borders.Add(src.Borders[i]);
                return w;
            }
        }
    }
}
