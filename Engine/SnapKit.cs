using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 贴合（snap）。把玩家点下的原始射线位置换成「贴着某个世界特征」的位置。
    ///
    /// 与游戏自带吸附的关系（FACT：AreaToolSystem.SnapJob ATS:63-1294 已经会贴
    /// 已有区域边界 / 道路曲线 / 建筑地块边 / 轴向锁 / 8m 网格）：我们不做「替代」，
    /// 而是把游戏算出来的那个点当作**又一个候选**一起打分。
    /// 好处：游戏已经处理好的细节（地块边、轴锁）我们白拿；我们只补它没有的两件事 ——
    /// 半径按类别分级放宽（需求 7）与海岸线（FACT：Snap.Shoreline 在 AreaToolSystem 里从未被使用，
    /// 只有 NetToolSystem.cs:1811 与 ObjectToolSystem.cs:976 用了，所以画区域贴不到海边是真缺口）。
    /// </summary>
    public static class SnapKit
    {
        /// <summary>
        /// 贴合的附加上下文。需求 3 的两条规则都要用到「已经画出来的部分」，只看当前这一次点击是算不出来的：
        ///  · 一条路有左右两条外沿，选哪一条要看**区域内部**在哪一侧（离质心近的那条才不把马路划进地块）；
        ///  · 产业区/垃圾场「不允许跨越道路或建筑」，要判的是「上一个手动节点 → 这一格候选」这条直线。
        /// 全默认构造（<c>new SnapExtra()</c>）= 两个判据都不参与，退化成老的一次点击独立判贴合。
        /// </summary>
        public struct SnapExtra
        {
            public bool HasCentroid;
            public P3 Centroid;
            public bool HasPrevious;
            public PlacedNode Previous;
        }

        /// <summary>路口判定容差（米）：相交点离某条边的任一端点小于它，就不算「横穿马路」，只是路过路口。</summary>
        private const double JUNCTION_TIE = 12.0;

        /// <summary>
        /// raw = 玩家射线命中的世界位置；gameSnapped = 游戏自己吸附后的位置（Kind=Free 表示不提供）。
        /// 返回一定可用的 PlacedNode（最差是 Kind=Free、位置=raw）。
        /// </summary>
        public static PlacedNode FindSnap(P3 raw, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, PlacedNode gameSnapped)
        {
            return FindSnap(raw, world, cfg, tuning, gameSnapped, new SnapExtra());
        }

        public static PlacedNode FindSnap(P3 raw, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, PlacedNode gameSnapped, SnapExtra extra)
        {
            if (cfg == null) cfg = ModConfig.CreateDefault();
            double radius = tuning.SnapRadius;
            if (!tuning.SnapEnabled || world == null || radius <= 0)
            {
                return gameSnapped.Kind == SnapKind.Free ? FreeNode(raw) : gameSnapped;
            }

            Best best = new Best(raw, cfg, tuning.Tier, radius);
            bool edgeTier = PolicyKit.UsesEdgeTargets(tuning.Tier);
            bool strict = PolicyKit.BlocksCrossing(tuning.Tier);
            P3 hit;
            int segIdx;
            double segT;
            double arc;

            // 游戏自己的吸附结果作为一个候选参与比较（它通常已经落在交叉口或地块边上）。
            if (gameSnapped.Kind != SnapKind.Free)
            {
                double gd = raw.DistanceTo(gameSnapped.Pos);
                if (gd <= radius * 1.5)
                {
                    best.Offer(gameSnapped.Pos, gameSnapped.Kind, gd, gameSnapped.GraphNode, gameSnapped.Edge, gameSnapped.Arc, gameSnapped.Signature, gameSnapped.Side);
                }
            }

            // 1) 网络节点（交叉口）。度数越高越像「玩家真正想点的那个角」。
            //    白名单：只考虑接着合法网络边（道路/轨道，非埠头、非内部路）的节点 ——
            //    只有步道交汇的那个点不是目标（反馈 9），而步道网比车行道密得多，
            //    正是反馈 1「空地上莫名其妙吸到鼠标旁边一个节点」的来源。
            //    路缘档（产业区/表面区）整体不用交叉口：那是中心线上的点（见 PolicyKit.KindAllowed）。
            for (int i = 0; i < world.Nodes.Count; i++)
            {
                GraphNode gn = world.Nodes[i];
                // 第八轮反馈 5：候选坐标用路口中心点，不用成员节点各自的位置
                //（同一路口的 2~5 个成员节点里，只有这个是「那个路口」）。
                P3 nodePos = world.NodePoint(i);
                double d = raw.DistanceTo(nodePos);
                if (d > radius) continue;
                // 锚点边必须是**白名单内**的那条：挑到步道上去，描边第一步就走出名单了。
                int anchorEdge = PolicyKit.AllowedEdgeOf(tuning.Tier, world, i);
                if (anchorEdge < 0) continue;
                double w = PolicyKit.KindWeight(cfg, SnapKind.NetNode);
                if (gn.Degree == 2) w *= 1.4;      // 只是同一条路的分段点，没那么值得贴
                else if (gn.Degree <= 1) w *= 1.9; // 断头路
                // Arc 必须是「这个交叉口在那条边上的弧长位置」（0 或全长），不能留 0：
                // 老实现一律给 0，于是当交叉口恰好是那条边的 EndNode 时，
                // 描边会从边的另一头整条拖进来（回归壳 T6：直线 60 m 的边描出 224 m 的折返）。
                best.OfferWeighted(nodePos, SnapKind.NetNode, d, w, i, anchorEdge, ArcOfNode(world, anchorEdge, i), gn.Signature, PlacedNode.SIDE_CENTRE);
            }

            // 2) 网络曲线。**贴哪一条线由类别决定**（需求 3 的第一半，口径见 PolicyKit.KindAllowed）：
            //    市辖区 → 中心线；产业区/地皮/表面 → 两条外沿，且拿不到外沿时**不**退回中心线
            //    （把地块角点吸到马路中心 = 半条马路划进地块，比不吸更错）。
            for (int e = 0; e < world.Edges.Count; e++)
            {
                GraphEdge ge = world.Edges[e];
                if (ge.Line == null || ge.Line.Count < 2) continue;
                if (ge.Line.Bounds().DistanceTo(raw) > radius) continue;
                // 白名单（反馈 9）：步行路、水道、埠头、建筑内部的路一律不是贴合目标，
                // 中心线档与路缘档都一样 —— 三类区域只认道路/轨道。
                if (!PolicyKit.NetAllowed(tuning.Tier, ge)) continue;

                if (!edgeTier)
                {
                    double dist = GeoKit.ClosestPointOnPolylineProbe(ge.Line, raw, out hit, out segIdx, out segT, out arc);
                    if (dist <= radius) best.Offer(hit, SnapKind.NetCentre, dist, -1, e, arc, ge.Signature, PlacedNode.SIDE_CENTRE);
                    continue;
                }

                // 地下不参与：游戏贴路缘时就是把 Tunnel 组合排除在外的（AreaToolSystem.cs:501-508）。
                // 第八轮反馈 3 之后这条其实已经被上面那句 NetAllowed（→ IsTargetNet → Underground）盖住了，
                // 留着是因为它在这里还有一层说明价值：地下的缘线吸出来玩家根本看不见。
                if (ge.Underground) continue;

                OfferSide(ref best, world, cfg, ge, e, PlacedNode.SIDE_LEFT, raw, radius, extra, strict);
                OfferSide(ref best, world, cfg, ge, e, PlacedNode.SIDE_RIGHT, raw, radius, extra, strict);
            }

            // 2b) 建筑轮廓（同一档只在路缘类里开放）。
            if (edgeTier)
            {
                for (int o = 0; o < world.Objects.Count; o++)
                {
                    ObjectRef or = world.Objects[o];
                    if (or.Line == null || or.Line.Count < 3) continue;
                    // 白名单（反馈 9）：只认**建筑**轮廓。游戏自己那一档对任何非圆形静态物体都贴边
                    // （FACT：AreaToolSystem.cs:651-663 只查 Circular），于是地块角点会被吸到一棵树、
                    // 一根高架桥墩的外接框上 —— 玩家看到的形状是「吸附目标根本不是建筑」。
                    if (!PolicyKit.ObjectAllowed(tuning.Tier, or)) continue;
                    if (or.Line.Bounds().DistanceTo(raw) > radius) continue;
                    double dist = GeoKit.ClosestPointOnPolylineProbe(or.Line, raw, out hit, out segIdx, out segT, out arc);
                    if (dist > radius) continue;
                    double w = PolicyKit.KindWeight(cfg, SnapKind.ObjectSide);
                    // 需求 3 要的是「**同一建筑**边缘」：上一个节点已经贴在这栋楼上时，这一格继续贴它更合意
                    // —— 一栋楼的轮廓本来就是一圈闭合折线，跟着走完比跳到路缘再跳回来顺。
                    if (extra.HasPrevious && extra.Previous.Kind == SnapKind.ObjectSide
                        && AnchorCode.IsObject(extra.Previous.Edge)
                        && AnchorCode.Decode(extra.Previous.Edge, AnchorCode.OBJECT_BASE) == o)
                    {
                        w *= 0.8;
                    }
                    best.OfferWeighted(hit, SnapKind.ObjectSide, dist, w, -1, AnchorCode.Object(o), arc, or.Signature, PlacedNode.SIDE_CENTRE);
                }
            }

            // 3) 已有区域边界（同类优先，异类次之）。负数 Edge 表示「这不是可描边的网络边」，
            //    编码规则见 TraceKit.AnchorEdgeOf：-(b+1)=区域边界，-(2e6+s)=海岸线，-(1e6+b)=瓦片边界。
            for (int b = 0; b < world.Borders.Count; b++)
            {
                BorderRef br = world.Borders[b];
                if (br.Line == null || br.Line.Count < 2) continue;
                if (br.Line.Bounds().DistanceTo(raw) > radius) continue;
                double dist = GeoKit.ClosestPointOnPolylineProbe(br.Line, raw, out hit, out segIdx, out segT, out arc);
                if (dist > radius) continue;
                SnapKind kind = br.Tier == tuning.Tier ? SnapKind.AreaBorderSame : SnapKind.AreaBorderOther;
                best.Offer(hit, kind, dist, -1, AnchorCode.Border(b), arc, br.Signature, PlacedNode.SIDE_CENTRE);
            }

            // 4) 地图瓦片边界。
            for (int b = 0; b < world.MapTiles.Count; b++)
            {
                BorderRef br = world.MapTiles[b];
                if (br.Line == null || br.Line.Count < 2) continue;
                if (br.Line.Bounds().DistanceTo(raw) > radius) continue;
                double dist = GeoKit.ClosestPointOnPolylineProbe(br.Line, raw, out hit, out segIdx, out segT, out arc);
                if (dist > radius) continue;
                best.Offer(hit, SnapKind.MapTileBorder, dist, -1, AnchorCode.Tile(b), arc, br.Signature, PlacedNode.SIDE_CENTRE);
            }

            // 曾经这里还有「5) 海岸线」一档：玩家实测那条开关看不出效果，v0.1.4 按要求整条去掉
            // （不再抓海岸线折线、不再吸、不再描）。世界快照里也就没有 Shorelines 这个表了。

            return best.ToNode(raw);
        }

        /// <summary>
        /// 把某条网络边的某一侧外沿作为一个候选递给打分器。三件事在这里办：
        ///  ① 只有这条边真有这一侧的折线才Offer（缺侧就少一档，绝不拿中心线顶替）；
        ///  ② 质心判侧：区域内部在哪边，就贴哪条路缘 —— 贴错侧等于把整条马路圈进地块；
        ///  ③ 严格档（产业区/垃圾场）里，「上一个节点 → 这一格候选」横穿了马路或建筑的候选直接不收。
        ///
        /// ⚠ <see cref="Best"/> 是 **struct**：不加 <c>ref</c> 时这里改的是副本，
        /// 路缘候选会在编译通过、测试全绿（因为没人报错）的情况下静默消失 ——
        /// 回归壳 N3 第一次就是这么抓出来的（「地块贴路缘」退化成贴到 30 米外的区域边界上）。
        /// </summary>
        private static void OfferSide(ref Best best, WorldSnapshot world, ModConfig cfg, GraphEdge ge, int edgeIndex,
                                      byte side, P3 raw, double radius, SnapExtra extra, bool strict)
        {
            Polyline line = side == PlacedNode.SIDE_LEFT ? ge.LeftLine : ge.RightLine;
            if (line == null || line.Count < 2) return;
            if (line.Bounds().DistanceTo(raw) > radius) return;

            P3 hit;
            int segIdx;
            double segT;
            double arc;
            double dist = GeoKit.ClosestPointOnPolylineProbe(line, raw, out hit, out segIdx, out segT, out arc);
            if (dist > radius) return;

            double w = PolicyKit.KindWeight(cfg, SnapKind.NetSide);
            if (extra.HasCentroid)
            {
                // 两侧都够近时偏向「离区域质心更近」的那条缘：质心在路的北侧 ⇒ 北侧路缘离质心近。
                // 权重越小越优先，所以近的一侧乘 0.8、远的一侧乘 1.25。
                double mine = DistanceToCentroid(line, extra.Centroid);
                double other = DistanceToCentroid(side == PlacedNode.SIDE_LEFT ? ge.RightLine : ge.LeftLine, extra.Centroid);
                if (double.IsPositiveInfinity(other)) { /* 对面没有线：这一侧本来就该赢 */ }
                else if (mine < other) w *= 0.8;
                else w *= 1.25;
            }
            if (strict && extra.HasPrevious && CrossesObstacle(world, extra.Previous.Pos, hit)) return;

            // 侧向粘性：上一个手动节点就贴在这条边的同一侧 ⇒ 这一格继续同侧优先。
            // 质心要第三点之后才有意义，而一条边上的第二格就要定侧了；另外区域画大之后质心离这段边界
            // 可能只隔十几米，两侧缘的差别只有厘米级，没有这条粘性的话鼠标一晃就会翻到对侧缘
            // （玩家视角是「同一条路上贴来贴去，边界突然跳到马路对面」）。
            if (extra.HasPrevious && extra.Previous.Kind == SnapKind.NetSide
                && extra.Previous.Edge == edgeIndex && extra.Previous.Side == side)
            {
                w *= 0.9;
            }

            best.OfferWeighted(hit, SnapKind.NetSide, dist, w, -1, edgeIndex, arc, ge.Signature, side);
        }

        /// <summary>一条外沿折线到区域质心的最短距离；线为 null 时返回 +∞（= 这一侧无条件占优）。</summary>
        private static double DistanceToCentroid(Polyline line, P3 centroid)
        {
            if (line == null || line.Count < 2) return double.PositiveInfinity;
            P3 dummy;
            int si;
            double st, al;
            return GeoKit.ClosestPointOnPolylineProbe(line, centroid, out dummy, out si, out st, out al);
        }

        /// <summary>
        /// 「这条直线是否横穿了马路或建筑」（需求 3 的跨越判据）。
        ///
        /// 三个条件同时成立才算跨越，缺一个就会大量误判，都是实际会画出来的形状：
        ///  ① 与某条路的中心线**严格相交**（贴着走、端点相触不算）；
        ///  ② 直线两端在这条路中心线的**异侧**。少了这条，沿着 A 路缘画到与 B 路相交的路口时，
        ///     直线会横穿 B 路中心线而两端同在 B 的北半边 —— 那是路过路口，不是跨越 B；
        ///  ③ 交点离这条路的两个端头都大于 <see cref="JUNCTION_TIE"/>。少了这条，
        ///     L 形拐角处「P 在 A 北缘、Q 在 B 东缘」会因为分居 B 中心线两侧而被判成跨越
        ///     —— 但交点就在路口上，这正是 ② 想放行的那种形状，只能靠位置区分。
        /// 建筑那一半只看严格相交：轮廓是闭合的，穿进去必进必出，没有路口可言。
        /// </summary>
        public static bool CrossesObstacle(WorldSnapshot world, P3 a, P3 b)
        {
            if (world == null) return false;
            Box2 chord = Box2.Empty();
            chord.Expand(a);
            chord.Expand(b);
            for (int e = 0; e < world.Edges.Count; e++)
            {
                GraphEdge ge = world.Edges[e];
                if (ge.Line == null || ge.Line.Count < 2) continue;
                // 与吸附同一份白名单（反馈 9 / 10）：步道、埠头、建筑内部路不是贴合目标，
                // 也就不该被当成"障碍物"把产业区的边界判成跨越 —— 两条规则用两份名单就是自相矛盾。
                if (!PolicyKit.IsTargetNet(ge)) continue;
                // 粗筛（包围盒）再进 O(段数) 的相交检测：这一步不省，贴合就是每候选 × 全走廊边数。
                if (!ge.Line.Bounds().Intersects(chord)) continue;
                P3 x;
                if (!GeoKit.SegmentCrossesPolyline(a, b, ge.Line, out x)) continue;
                double sa = SignedSide(ge.Line, a);
                double sb = SignedSide(ge.Line, b);
                if (sa == 0 || sb == 0) continue;                  // 端点就压在中心线上：贴着走，不算跨越
                if ((sa > 0) == (sb > 0)) continue;                // 同侧 ⇒ 只是路过路口
                // ③ 交点在这条边的哪一头附近？在 ⇒ 路口，放行。
                double head = x.DistanceTo(ge.Line.Points[0]);
                double tail = x.DistanceTo(ge.Line.Points[ge.Line.Count - 1]);
                if (head < JUNCTION_TIE || tail < JUNCTION_TIE) continue;
                return true;
            }
            for (int o = 0; o < world.Objects.Count; o++)
            {
                ObjectRef or = world.Objects[o];
                if (or.Line == null || or.Line.Count < 3) continue;
                if (!or.IsBuilding) continue;      // 摆件/树/桥墩：既不是目标，也不算「跨了一栋楼」
                if (!or.Line.Bounds().Intersects(chord)) continue;
                // 闭合轮廓：穿进去必进必出，所以严格相交就是「边界跨了一栋楼」。
                if (GeoKit.SegmentCrossesPolyline(a, b, or.Line)) return true;
            }
            return false;
        }

        /// <summary>
        /// 点相对折线的**有符号**侧向：+ 在局部行进方向的左手侧，- 在右手侧，0 在线上。
        /// 折线在游戏侧抓取时已被统一成「起点 = 这条边 StartNode 那一头」的同向
        /// （WorldSampler.StitchEdge / OffsetLine），所以同一条边上取到的符号互相可比 ——
        /// 这是跨越判据成立的前提。方向没统一时这里会给出随机符号，跨越就会随机漏判。
        /// </summary>
        public static double SignedSide(Polyline line, P3 p)
        {
            if (line == null || line.Count < 2) return 0;
            P3 best;
            int segIdx;
            double segT, arc, dist;
            GeoKit.ClosestPointOnPolyline(line, p, out best, out segIdx, out segT, out arc, out dist);
            if (dist <= 1e-6) return 0;
            if (segIdx < 0 || segIdx + 1 >= line.Count) return 0;
            V2 dir = (V2.From(line.Points[segIdx + 1]) - V2.From(line.Points[segIdx])).Normalized();
            V2 off = V2.From(p) - V2.From(best);
            double cross = dir.CrossZ(off);
            if (cross > 1e-9) return 1;
            if (cross < -1e-9) return -1;
            return 0;
        }

        /// <summary>最终仍要过一道「别把玩家点的位置挪过头」的闸：超过半径就不算贴上了。
        /// public 是给 <see cref="FollowKit"/> 用的：反推锚点时被「不许拖走玩家几何」拦下的那格就是自由点。</summary>
        public static PlacedNode FreeNode(P3 p)
        {
            return new PlacedNode(p, SnapKind.Free, -1, -1, 0, 0);
        }

        /// <summary>
        /// 交叉口锚点在指定边上的弧长位置（0 或整条边长）。
        /// public 是给 <see cref="AnchorRebind"/> 用的：重绑到新快照后要按新下标重新量一次弧长。
        /// </summary>
        public static double ArcOfNode(WorldSnapshot world, int edgeIdx, int nodeIdx)
        {
            if (edgeIdx < 0 || edgeIdx >= world.Edges.Count) return 0;
            GraphEdge ge = world.Edges[edgeIdx];
            if (ge == null || ge.StartNode == nodeIdx) return 0;
            double total = ge.Length > 0 ? ge.Length : (ge.Line != null ? ge.Line.Length2D() : 0);
            return ge.EndNode == nodeIdx ? total : 0;
        }

        /// <summary>
        /// 给交叉口随便挑一条所属边当描边锚点（哪个方向更合理由 TraceKit 的搜索决定）。
        /// public 同 <see cref="ArcOfNode"/>：重绑时要在新的邻接表上重挑一次。
        /// </summary>
        public static int NearestEdgeOf(WorldSnapshot world, int nodeIndex)
        {
            if (nodeIndex < 0) return -1;
            List<WorldSnapshot.AdjEntry> adj = world.Adjacency[nodeIndex];
            for (int i = 0; i < adj.Count; i++)
            {
                if (adj[i].EdgeIndex >= 0 && adj[i].EdgeIndex < world.Edges.Count) return adj[i].EdgeIndex;
            }
            return -1;
        }

        /// <summary>
        /// 候选收集器：score = 距离 × 类别权重，最小者胜。
        /// 分数打平（叠置路网的典型形状）时不再"保留先来者"，而是按**高度低者胜** —— 见 <see cref="TIE_EPS"/>。
        /// </summary>
        private struct Best
        {
            /// <summary>
            /// 分数平局的容差。平局不再是"保留先来者"，而是交给高度（反馈 8：叠置路网按最低的那条定节点）——
            /// 遍历顺序是四叉树的实现细节，跟着它走的结果就是同一次点击两次吸到不同的路上。
            /// </summary>
            private const double TIE_EPS = 0.05;

            private readonly ModConfig _cfg;
            private readonly AreaTier _tier;
            private readonly double _radius;

            private bool _has;
            private P3 _pos;
            private SnapKind _kind;
            private int _graphNode;
            private int _edge;
            private double _arc;
            private long _signature;
            private byte _side;
            private double _score;

            public Best(P3 raw, ModConfig cfg, AreaTier tier, double radius)
            {
                _cfg = cfg;
                _tier = tier;
                _radius = radius;
                _has = false;
                _pos = raw;
                _kind = SnapKind.Free;
                _graphNode = -1;
                _edge = -1;
                _arc = 0;
                _signature = 0;
                _side = PlacedNode.SIDE_CENTRE;
                _score = double.PositiveInfinity;
            }

            public void Offer(P3 pos, SnapKind kind, double dist, int graphNode, int edge, double arc, long sig, byte side)
            {
                OfferWeighted(pos, kind, dist, PolicyKit.KindWeight(_cfg, kind), graphNode, edge, arc, sig, side);
            }

            public void OfferWeighted(P3 pos, SnapKind kind, double dist, double weight, int graphNode, int edge, double arc, long sig, byte side)
            {
                // 分档闸：市辖区在这一层就收不到 NetSide 候选，产业区收不到 NetCentre ——
                // 而不是「都收进来再靠权重压」。权重是偏好，能压错；这一档是语义，压不错。
                if (!PolicyKit.KindAllowed(_cfg, _tier, kind)) return;
                if (dist > _radius) return;
                double score = dist * weight;
                // 叠置路网（反馈 8）：高架正好压在地面路上时，两条路给出的 2D 距离**严格相等**
                //（P3.DistanceTo 只看 X/Y），老口径「严格小于才胜出」就把胜负交给了四叉树的遍历顺序 ——
                // 玩家看到的是同一次点击一会儿吸地面路、一会儿吸高架。按玩家的口径：高度最低的那条胜。
                if (_has && Math.Abs(score - _score) <= TIE_EPS && pos.H < _pos.H - PolicyKit.HEIGHT_TIE_EPS)
                {
                    _score = score;      // 分数一样，只换落点与锚点信息
                    _pos = pos;
                    _kind = kind;
                    _graphNode = graphNode;
                    _edge = edge;
                    _arc = arc;
                    _signature = sig;
                    _side = side;
                    return;
                }
                if (score < _score - 1e-12)
                {
                    _score = score;
                    _has = true;
                    _pos = pos;
                    _kind = kind;
                    _graphNode = graphNode;
                    _edge = edge;
                    _arc = arc;
                    _signature = sig;
                    _side = side;
                }
            }

            public PlacedNode ToNode(P3 raw)
            {
                if (!_has) return new PlacedNode(raw, SnapKind.Free, -1, -1, 0, 0);
                return new PlacedNode(_pos, _kind, _graphNode, _edge, _arc, _signature, _side);
            }
        }
    }
}
