using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>描边为什么回退成直线。写进日志与提示，实机调参时不用猜。</summary>
    public enum TraceFallback
    {
        None = 0,
        Disabled,
        NotOnNetwork,
        SameAnchor,
        NoPathWithinBudget,
        DetourTooLong,
        WouldSelfIntersect,
        WouldDegenerate,
        NodeBudget,
        /// <summary>
        /// 这一段的两个锚点分居同一条路的两侧、或直线横穿了马路/建筑。
        /// 需求 3：表面类「跨越部分的直线不需要做自动贴合」⇒ 这里明确回退成玩家自己画的那条直线，
        /// 而不是硬找一条绕行路径（找到了也是把马路圈进地块）。
        /// </summary>
        CrossesObstacle,
    }

    /// <summary>一条边的描边结果。</summary>
    public sealed class TraceResult
    {
        /// <summary>中间点，**不含**两端的手动节点（投影层自己按不变量插位置）。</summary>
        public List<P3> Points = new List<P3>();
        public bool UsedNetwork;
        public bool UsedBorder;
        /// <summary>
        /// 任何简化闸都不许删的位置（需求 2：网络类型变化处的那个交叉点必须留一个节点）。
        /// 由 <see cref="ViaGraph"/> 在拼接走线时登记，交给 <see cref="SimplifyKit.FitToBudget"/>。
        /// </summary>
        public readonly List<P3> Pins = new List<P3>();
        public double Cost;
        public TraceFallback Fallback = TraceFallback.None;
        /// <summary>本次描边依赖的世界几何签名：签名没变 ⇒ 结果可以复用（需求 5 的自动跟随靠它做增量大算）。</summary>
        public long Signature;
        /// <summary>算出这条结果时整张世界快照的签名。缓存复用的判据用它，而不是用 Signature（后者是边的组合）。</summary>
        public long WorldSignature;
        public Box2 Corridor = Box2.Empty();
        /// <summary>用到的网络边下标，脏矩形判定用。</summary>
        public readonly List<int> UsedEdges = new List<int>();

        /// <summary>
        /// 这条结果是**续接路径**（反馈 7）：中间有一段沿网络自动贴合，末尾用一条直线接上那个不在网络上的端点。
        /// 走线的最后一个中间点就是「中断处」那一格 —— 玩家说的「续接的节点可以是手动节点，也可以是续接路径上的节点」
        /// 里的那个节点。日志与预览都要能把它和纯粹的自动点区分开。
        /// </summary>
        public bool Continued;

        /// <summary>
        /// 本条走线里有多少个点是**照抄邻接区域节点的坐标**（第六轮反馈 7，见 AdoptBorderNodes）。
        /// 引擎层没有日志器（它必须在零游戏引用的离线壳里编译），所以用这个数字把证据带出去，
        /// 由 GameSide 累进 <c>SnapperState.AdoptedNodes</c> 打进统计行 ——
        /// 实机验收时"两块地共用一段路，节点到底有没有取齐"就看它是不是在涨。
        /// </summary>
        public int Adopted;

        /// <summary>
        /// 这一条边加了几个**接入点**（0/1/2）。第八轮反馈 2/3/10 的接入点模型：玩家那一格不动，
        /// 走线在它旁边另找一个自动节点接进网络。引擎层没有日志器（同上），所以把这个数带出去，
        /// 由 GameSide 累进 <c>SnapperState.AttachedPoints</c> 打进统计行的 <c>attach=</c>。
        /// 实机判据：反馈 2「关掉节点续接以后预览和贴合直接不生效」修没修上，就看
        /// <c>attach&gt;0</c> 与 <c>cont=0</c> 同时成立 —— 能连通的路现在走的是描边那条路，不再走续接。
        /// </summary>
        public int Attached;

        /// <summary>
        /// 这一条边上有几个交付顶点被从**路口的某个成员节点**挪到了那个路口的中心点
        /// （第八轮反馈 5，见 <c>ClampToJunctions</c>）。引擎层带不出日志，所以照
        /// <see cref="Adopted"/> / <see cref="Attached"/> 的规矩把这个数交出去，
        /// 由 GameSide 累进 <c>SnapperState.JunctionClamps</c> 打进统计行的 <c>junc=</c>。
        /// 实机判据：玩家那两张截图里「在偏心几米的那一格拐弯」改没改掉就看它涨不涨 ——
        /// <c>junc=0</c> 而路口形状照旧 ⇒ 归并在真实数据上没并出路口（<c>MERGE_SPAN</c> 太窄或
        /// 合法度数判据太严），那是回去调常数，不是改结构。
        /// </summary>
        public int Clamped;

        public int MiddleCount { get { return Points.Count; } }
    }

    /// <summary>描边上下文：绕行方向与退化判定需要「已经画出来的那部分环」的信息。</summary>
    public struct TraceContext
    {
        /// <summary>已有手动节点的质心（判断绕行该走哪一侧包围街区）。</summary>
        public bool HasCentroid;
        public P3 Centroid;
        /// <summary>已确定的边界折线（含本条边两端的直连试探），用于自交检测。</summary>
        public List<P3> ExistingRing;
        /// <summary>多边形绕向：+1 逆时针，-1 顺时针。0 表示还没定（节点不足 3 个）。</summary>
        public double Orientation;
        /// <summary>
        /// 本条描边替换的是 <see cref="ExistingRing"/> 里第几格（该格 = ExistingRing[RingEdge] →
        /// ExistingRing[RingEdge+1]，闭合环的最后一格回指 0）。-1 = 调用方不知道（绘制中的开放路径就是这样）。
        ///
        /// 【为什么要有这一格】自交判定要问的是「换成这条走线之后整个边界还不自交」，所以得把走线
        /// 插回它替换的那一格。只靠**位置**去找那一格在两种情况下会找不到：
        ///  ① 拖拽改形：环里那一格存的还是玩家刚拖到的位置，而我们递进去的端点是吸附之后的位置；
        ///  ② 跟随重描：环里的点已经是我们上一次写的密点，两端各有好几格重合同一条线。
        /// 找不到就退回「环 + 整条走线首尾相接」的老写法 —— 那等于在环上多画一条弦，
        /// 每条边都判成 WouldSelfIntersect（离线壳 X9/X9c 两次都是这么抓到的）。
        ///
        /// ⚠ 结构体字段不能带初始化表达式（C# 9：CS8773/CS8983），所以这里是一对
        /// <c>HasRingEdge</c> + <c>RingEdge</c>，而不是一个默认 -1 的 int ——
        /// 只写 int 的话 <c>default(TraceContext)</c> 会带着 RingEdge=0 进来，
        /// 于是所有没显式报格号的调用都会被强行替换掉第 0 格，判出完全不同的自交结果。
        /// </summary>
        public bool HasRingEdge;
        public int RingEdge;
    }

    /// <summary>
    /// 描边（trace）：把「两个手动节点之间」从直线换成**沿网络走过去**的折线。
    ///
    /// 与原模组（Subdivisions，已下架，无源码）的差别写在设计文档 §四：
    /// 它是「同 kind 才描边，跨 kind 一律直线」；我们是「跨 kind 也描，但给转弯/换类加代价，
    /// 代价或绕行比超预算就回退直线」——既保住它的稳定性（它的更新日志里两次在修边界自交与薄片退化），
    /// 又不会出现「两个节点跨了一条路和铁路就变成直线」。
    /// </summary>
    public static class TraceKit
    {
        // 换网络类型的代价与转弯代价不再是文件级常量：第五轮反馈 5 把它们并进「贴合模式」，
        // 每个模式一组数字（PolicyKit.ModeKnobs），因为「怕不怕拐弯」本来就是模式之间的区别。

        public static TraceResult Trace(PlacedNode a, PlacedNode b, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, TraceContext ctx)
        {
            // ————————————————————————————— 接入点（第八轮反馈 2/3/6/8/10 的共同修法）
            //
            // 【要解决的架构缺陷】v0.1.4 起「玩家点下的那一格不再由模组吸附」（第八轮反馈 3/10 又强调了一次），
            // 于是端点的 Kind 基本是 Free；而下面那个 `IsTraceable` 闸要求**两端都贴在能描的东西上**才肯描边
            // ⇒ 正常描边这条路几乎永远走不到，唯一还能产出网络点的只剩「节点续接」那一支。
            // 玩家看到的三件事全是这一个缺陷的三面：
            //  · 反馈 2「关掉节点续接后预览和贴合直接不生效」—— 因为续接就是唯一的网络来源；
            //  · 反馈 2「开着续接变成强行续接」—— 因为能正常连通的两端也被丢进续接那条**贪心单跳**的支路
            //    （它每步只挑「离自由端更近」的一条边，转角一超 75° 就断，从来不走 A*）；
            //  · 反馈 4/6/8「不显示路径 / 太远就直连 / 产业区几乎不贴」—— 同一道闸在不同形状上的表现。
            //
            // 【改法】端点**一动不动**，模组在它吸附距离内另找一个**接入锚点**，走线 =
            // 玩家端点 → 接入点 → 沿网络（经路口中心点）→ 接入点 → 玩家端点。
            // 接入点是自动节点，按这一档的规则吸（中心线档吸中心线、路缘档吸人行道外缘/建筑轮廓）——
            // 正是反馈 10 那句「只有自动贴合路径上的节点需要按规则吸附」。
            PlacedNode freeA = a, freeB = b;
            if (world != null && cfg != null && tuning.TraceEnabled)
            {
                bool et = PolicyKit.UsesEdgeTargets(tuning.Tier);
                double gap = Math.Max(tuning.MinSpacing, 0.5);
                PlacedNode ta, tb;
                if (!StuckOnTarget(a, world, et, gap) && Attach(a, world, cfg, tuning, ctx, et, out ta)) a = ta;
                if (!StuckOnTarget(b, world, et, gap) && Attach(b, world, cfg, tuning, ctx, et, out tb)) b = tb;
                TraceResult core = Trace(a, b, world, cfg, tuning, ctx, true);
                TraceResult led = Lead(core, freeA, freeB, a, b, tuning, ctx, world);
                // 前导线自己横穿了马路/建筑 ⇒ 这个接入点不该用（等于把边界直直画过一块街区）。
                // 退回「按玩家那两个端点原样走一遍」：那里会自然落到续接/直线那两条老路上。
                if (led.Fallback == TraceFallback.CrossesObstacle) return Trace(freeA, freeB, world, cfg, tuning, ctx, true);
                return led;
            }
            return Trace(a, b, world, cfg, tuning, ctx, true);
        }

        /// <summary>
        /// 在端点自己的吸附距离内找一个接入锚点。找不到（周围真没有本档该贴的东西）返回 null，
        /// 调用方保留那个自由端点 ⇒ 走线自然退成续接或直线。
        /// 候选打分与「玩家点一格时」用的是**同一个** FindSnap，所以「吸上来在哪」与「接进来在哪」不会两套口径。
        /// </summary>
        private static bool Attach(PlacedNode end, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning,
                                   TraceContext ctx, bool edgeTier, out PlacedNode attach)
        {
            attach = default(PlacedNode);
            if (!tuning.SnapEnabled || tuning.SnapRadius <= 0) return false;
            SnapKit.SnapExtra extra = new SnapKit.SnapExtra();
            if (ctx.HasCentroid) { extra.HasCentroid = true; extra.Centroid = ctx.Centroid; }
            PlacedNode t = SnapKit.FindSnap(end.Pos, world, cfg, tuning, SnapKit.FreeNode(end.Pos), extra);
            if (!IsTraceable(t, edgeTier)) return false;
            attach = t;
            return true;
        }

        /// <summary>
        /// 把两端各接一小段「前导线」并**用玩家端点重过一遍闸**。
        /// 两条限制都是这条路上最容易出的新问题：
        ///  · 接入点离玩家那一格不到一格间距时不加 —— 否则直线上会凭空多两个几乎重合的自动点
        ///    （第六轮反馈 1 那个「直线上不该多节点」的同一种症状，只是这次的来源是接入点）；
        ///  · 前导线自己横穿马路/建筑时不加（登记 CrossesObstacle，调用方退回不接）。
        ///    这一条是第八轮反馈 9 的另一半：否决权从「两点的连线」挪到了「真正要画出去的那一段」上。
        /// </summary>
        private static TraceResult Lead(TraceResult r, PlacedNode freeA, PlacedNode freeB,
                                        PlacedNode a, PlacedNode b, ResolvedTuning tuning, TraceContext ctx,
                                        WorldSnapshot world)
        {
            if (r == null) return r;
            if (!(r.UsedNetwork || r.UsedBorder)) return r;
            double gap = Math.Max(tuning.MinSpacing, 0.5);
            bool leadA = freeA.Pos.DistanceTo(a.Pos) > gap;
            bool leadB = freeB.Pos.DistanceTo(b.Pos) > gap;
            if (!leadA && !leadB) return r;

            if (leadA && SnapKit.CrossesObstacle(world, freeA.Pos, a.Pos))
            {
                r.Fallback = TraceFallback.CrossesObstacle;
                return r;
            }
            if (leadB && SnapKit.CrossesObstacle(world, freeB.Pos, b.Pos))
            {
                r.Fallback = TraceFallback.CrossesObstacle;
                return r;
            }

            if (leadB) r.Points.Add(b.Pos);
            if (leadA) r.Points.Insert(0, a.Pos);
            r.Attached = (leadA ? 1 : 0) + (leadB ? 1 : 0);
            r.Cost += (leadA ? freeA.Pos.DistanceTo(a.Pos) : 0) + (leadB ? freeB.Pos.DistanceTo(b.Pos) : 0);

            // 前导线是玩家自己那一笔的延长，必须跟走线一起过闸：自交、薄片退化、绕行比都按
            // **玩家两端**重算一遍（Accept 里算的是接入点两端，不含这两段）。
            TraceFallback why = Rejects(r, freeA, freeB, world, ctx, tuning, freeA.Pos.DistanceTo(freeB.Pos));
            if (why != TraceFallback.None)
            {
                r.Points.Clear();
                r.Pins.Clear();
                r.UsedNetwork = false;
                r.UsedBorder = false;
                r.Attached = 0;
                r.Fallback = why;
            }
            return r;
        }

        /// <summary>
        /// <paramref name="allowContinuation"/> 只有一处会传 false：<see cref="Continuation"/> 自己 ——
        /// 它内部就是拿「网络端 → 中断点」再走一遍本函数，若那一遍又不连续，续接就变成套娃了。
        /// </summary>
        private static TraceResult Trace(PlacedNode a, PlacedNode b, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning,
                                         TraceContext ctx, bool allowContinuation)
        {
            TraceResult r = new TraceResult();
            if (cfg == null) cfg = ModConfig.CreateDefault();
            if (!tuning.TraceEnabled || world == null)
            {
                r.Fallback = TraceFallback.Disabled;
                return r;
            }
            double straight = a.Pos.DistanceTo(b.Pos);
            if (straight < tuning.MinSpacing)
            {
                r.Fallback = TraceFallback.SameAnchor;
                return r;
            }

            // —— 0) 跨越闸（需求 3）：两个锚点分居同一条路的两侧，或直线横穿了马路/建筑时，
            //      这一段**不贴合**，留给玩家自己画的那条直线。
            //      严格档（产业区/垃圾场）在贴合阶段就已经拒收这类候选，走到这里的主要是表面档；
            //      两条规则的区别就在这个函数里：跨越时严格档连描边也不给，表面档同样不给直线保留 ——
            //      真正的差别在 SnapKit.OfferSide（严格档连点都不吸过去）。
            bool edgeTier = PolicyKit.UsesEdgeTargets(tuning.Tier);
            // 「这一端算不算已经贴在几何上」的容差，与 <see cref="Lead"/> 加前导线的门槛是同一个数：
            // 差得比一格间距还小 ⇒ 加节点纯属多余；差得更大 ⇒ 必须让走线自己长出接入点。
            double gap = Math.Max(tuning.MinSpacing, 0.5);
            // 第八轮反馈 9：连线压到东西**不再是一票否决**。
            // 老写法在这里直接 return，等于「玩家那一笔穿过了别的区域 ⇒ 这一格干脆不贴合」——
            // 可他真正问的是「贴出来的路径干不干净」：走线沿路绕过去、一个自交都没有，凭什么不给建？
            // 症状是他写的第二半：那种形状既不贴合也不给创建。
            // 现在闸只留在**结果**上，一共三处，各管各的：
            //  · <see cref="Lead"/>：接入点那两根**前导线**不许横穿马路/建筑（用同一个 SnapKit.CrossesObstacle）；
            //  · <see cref="Rejects"/>：整条走线（含前导线）不许自交、不许压成薄片；
            //  · 绕行比：<see cref="DetourBudget"/> —— 比例闸还在，只是不再是「距离一远就不许贴」。
            // 原来那个两端点之间的 WouldCross 判据随之删除（它唯一的作用就是这道否决）。

            // —— 0b) 两端必须都贴在「这一档该贴的东西」上才描边。
            //      旧口径到这里就整条退回玩家画的直线（需求 3 那句「首尾有一个点不在路上就不用自动贴合」），
            //      第五轮反馈 4/7 把它改窄了：**不在路上的那一端照样是它自己**，而从在路上那一端起的那一段
            //      仍然要贴 —— 这就是「节点续接」（见 <see cref="Continuation"/>）。
            //      续接关掉时才回到老行为：整条手动路径。
            //      ⚠ 判据是 StuckOnTarget 而不是 IsTraceable：第八轮反馈 3/10 之后玩家的坐标不许被改，
            //      于是「Kind 说在路上、坐标却差着一截」的锚点是**正常输入**（跟随/拖拽带回来的就是这种）。
            //      只看 Kind 会让走线沿路走完后凭空斜着接到那一格，那一跳既没有节点也没人管它跨了什么。
            if (!StuckOnTarget(a, world, edgeTier, gap) || !StuckOnTarget(b, world, edgeTier, gap))
            {
                r.Fallback = TraceFallback.NotOnNetwork;
                if (allowContinuation && tuning.ContinuationEnabled)
                {
                    TraceResult cont = Continuation(a, b, world, cfg, tuning, ctx, straight, edgeTier);
                    if (cont != null) return cont;
                }
                return r;
            }

            // （原来的「1) 海岸线」分支已随功能移除，见 WorldSnapshot / SnapKit 的同名说明。）

            // —— 2) 已有区域边界（同一条边界折线的两个弧位置之间直接切子段）。
            if (AnchorCode.IsBorder(a.Edge) && AnchorCode.IsBorder(b.Edge))
            {
                int ba = -(a.Edge + 1);
                int bb = -(b.Edge + 1);
                if (ba >= 0 && ba < world.Borders.Count && ba == bb)
                {
                    FillFromPolyline(r, world.Borders[ba].Line, a.Arc, b.Arc, a.Pos, b.Pos, tuning, false);
                    r.UsedBorder = true;
                    r.Signature = world.Borders[ba].Signature;
                    Accept(r, a, b, world, cfg, ctx, tuning, straight);
                    return r;
                }
                r.Fallback = TraceFallback.NotOnNetwork;
                return r;
            }

            // —— 2b) 建筑轮廓（需求 3：产业区/地皮类贴「同一建筑边缘」）。
            //      轮廓本身是闭合折线，两个锚点在同一栋楼上时沿轮廓走子段即可；
            //      「走哪一圈」不需要绕图搜索 —— 矩形轮廓只有两条路可选，短的那条就是沿着楼边走。
            if (AnchorCode.IsObject(a.Edge) && AnchorCode.IsObject(b.Edge))
            {
                int oa = AnchorCode.Decode(a.Edge, AnchorCode.OBJECT_BASE);
                int ob = AnchorCode.Decode(b.Edge, AnchorCode.OBJECT_BASE);
                if (oa >= 0 && oa < world.Objects.Count && oa == ob)
                {
                    ObjectRef or = world.Objects[oa];
                    FillFromPolyline(r, or.Line, a.Arc, b.Arc, a.Pos, b.Pos, tuning, false);
                    r.UsedBorder = true;
                    r.Signature = or.Signature;
                    Accept(r, a, b, world, cfg, ctx, tuning, straight);
                    return r;
                }
                r.Fallback = TraceFallback.NotOnNetwork;
                return r;
            }

            // —— 3) 网络。
            bool aNet = AnchorCode.IsNet(a.Edge);
            bool bNet = AnchorCode.IsNet(b.Edge);
            if (!aNet || !bNet)
            {
                // 一端/两端贴在「已有区域边界」或「建筑轮廓」上而不是网络上：那种走线在下面 2)/2b) 已经处理过，
                // 走到这里说明两边各自贴的东西配不成对（一条边界 + 一条路）⇒ 整条按玩家自己画的直线。
                r.Fallback = TraceFallback.NotOnNetwork;
                if (allowContinuation && tuning.ContinuationEnabled)
                {
                    TraceResult cont = Continuation(a, b, world, cfg, tuning, ctx, straight, edgeTier);
                    if (cont != null) return cont;
                }
                return r;
            }

            // 越界防御：手动栈是跨帧持有的，而走廊快照每帧重建、边下标会变。
            // 老实现只写了一句 `ea == null` 判空 —— List 下标越界是先抛 ArgumentException，
            // 那句永远不生效（回归壳 T7 抓到）。
            if (a.Edge >= world.Edges.Count || b.Edge >= world.Edges.Count)
            {
                r.Fallback = TraceFallback.NotOnNetwork;
                return r;
            }

            GraphEdge ea = world.Edges[a.Edge];
            GraphEdge eb = world.Edges[b.Edge];
            if (ea == null || eb == null || ea.Line == null || eb.Line == null)
            {
                r.Fallback = TraceFallback.NotOnNetwork;
                return r;
            }

            // 走哪一条线：中心线 / 左缘 / 右缘。两端同侧 ⇒ 那一侧；只有一端有侧 ⇒ 跟着那一端；
            // 都没有 ⇒ 中心线档（交叉口锚点就是这种）。
            byte side = TraceSide(a, b);

            // —— 反馈 9/10 的底层修正：路缘档（产业区/表面区）**绝不**沿中心线描边。
            //    两端都没有侧时（游戏自己吸到的是路口中心、或重绑回来的老锚点）TraceSide 给出 SIDE_CENTRE，
            //    而 LineFor(CENTRE) 就是中心线 ⇒ 边界沿马路中间走，半条马路被划进地块，
            //    玩家看到的形状是「贴到了完全不该贴的地方」。
            //    先用区域质心定侧（质心在哪一侧就贴哪一侧的外沿）；连质心都判不出来就**不描边**，
            //    交给续接/直线 —— 宁可直连，也不能把边界画到马路中间。
            if (edgeTier && side == PlacedNode.SIDE_CENTRE)
            {
                side = SideTowardCentroid(ea, ctx);
                if (side == PlacedNode.SIDE_CENTRE)
                {
                    r.Fallback = TraceFallback.NotOnNetwork;
                    if (allowContinuation && tuning.ContinuationEnabled)
                    {
                        TraceResult cont = Continuation(a, b, world, cfg, tuning, ctx, straight, edgeTier);
                        if (cont != null) return cont;
                    }
                    return r;
                }
            }

            // 3a. 同一条边：先按弧长切子段（这是最常见也最便宜的情况）。
            if (a.Edge == b.Edge)
            {
                TraceResult direct = new TraceResult();
                // 弧长要按**这条线自己**量（一侧是交叉口锚点、另一侧是路缘锚点时两者口径不同），
                // 否则子段会从边的另一头开始切 —— 见 ArcOnLine。
                Polyline sameLine = ea.LineFor(side);
                double sameTotal = Total(ea, side);
                FillFromPolyline(direct, sameLine, ArcOnLine(a, sameLine, side, sameTotal),
                    ArcOnLine(b, sameLine, side, sameTotal), a.Pos, b.Pos, tuning, true);
                direct.Signature = ea.Signature;
                direct.UsedNetwork = true;
                direct.UsedEdges.Add(a.Edge);

                TraceFallback why = Accept(direct, a, b, world, cfg, ctx, tuning, straight);
                if (why == TraceFallback.None) return direct;

                // 反馈 8（第一轮）/原模组 0.2.3 的「薄片」bug：两点在同一条环形路上时，直切子段会把区域压成一条缝。
                // 判定为坏结果时，改走图搜索绕另一侧 —— 注意绕的仍然是**这个环自己**（反馈 7：环岛沿它的中心走，
                // 不绕到外围街区那一圈去）。
                if (ea.OnRing || world.HasAlternatePathBetween(ea.StartNode, ea.EndNode, a.Edge))

                {
                    TraceResult around = ViaGraph(a, b, world, cfg, tuning, a.Edge, side, ctx, 0);
                    if (around.Fallback == TraceFallback.None
                        && Accept(around, a, b, world, cfg, ctx, tuning, straight) == TraceFallback.None)
                    {
                        return around;
                    }
                }
                // 关键修正：坏结果**不能**照样交出去。老代码在这里 fallthrough 到
                // Finish(direct, …)，等于「判定说不行、然后还是把这条 6 倍绕路的线画出来」
                // （回归壳 T5：直线 100 m 的边描出 608 m）。
                r.Fallback = why;
                return r;
            }

            // 3b. 不同边：A* 走廊搜索。
            if (!tuning.Knobs.AllowNetworkSwitch && ea.Kind != eb.Kind)
            {
                r.Fallback = TraceFallback.NotOnNetwork;
                return r;
            }
            // 需求 2 的「按最外围的路段贴合」。做法是**分别朝弦的两侧各搜一遍**，再和默认的最短路一起比：
            //   · 中间那条小巷几乎与两端的连线重合 ⇒ 它永远是「最短」的那条，只加惩罚项是换不掉它的
            //     （先按「离质心越近越贵」试过 OUTER_PENALTY 0.6 与 6.0：质心在哪个方向、块有多扁，
            //      都会让罚不动或罚过头，两个取值都测过，都不成立）；
            //   · 把「走左侧的路」和「走右侧的路」当成两个独立约束各搜一次，才真的把外围那条路取回来。
            //     落在弦上（±CHORD_SLACK 米以内）的边两边都放行，否则两点正对时会两遍都搜不到路。
            //   · 三条候选用「离区域质心的平均距离」定胜负 ⇒ 包围住街区外侧的那条赢；
            //     绕行比那道闸（Rejects 里用 res.Cost = 纯几何长度）仍然封顶 ⇒ 不会绕大圈。
            // 没有质心（手动节点不足 3 个）时只跑默认那一遍：头两条边没有「内部」可言。
            //
            // 两道额外的闸（都来自第五轮反馈 7）：
            //  · **贴合模式**说不许跳路（NoNetworkSwitch）或说不许为外围绕远（Shortest）⇒ 不做这两遍；
            //  · 两端里有任何一端贴在**环形路/环岛**上 ⇒ 不做「绕外围」那两遍：环岛就沿它自己那条线走，
            //    不刻意绕到环岛外侧那一圈路上去。
            TraceResult via = ViaGraph(a, b, world, cfg, tuning, -1, side, ctx, 0);
            bool ringCase = ea.OnRing || eb.OnRing;
            if (ctx.HasCentroid && tuning.Knobs.PreferOutermost && !ringCase)
            {
                TraceResult left = ViaGraph(a, b, world, cfg, tuning, -1, side, ctx, +1);
                TraceResult right = ViaGraph(a, b, world, cfg, tuning, -1, side, ctx, -1);
                via = BetterRoute(via, left, ctx);
                via = BetterRoute(via, right, ctx);
            }
            if (via.Fallback != TraceFallback.None) return via;
            Accept(via, a, b, world, cfg, ctx, tuning, straight);
            return via;
        }


        /// <summary>弦的侧向容差（米）：中点落在这条带里的边算「压在弦上」，两种侧向搜索都放行。</summary>
        private const double CHORD_SLACK = 2.0;

        /// <summary>
        /// 绕行预算 = <c>直线距离 × MaxDetourFactor</c>，**只有比例、不加绝对余量**。
        ///
        /// 第八轮反馈 6：「下个节点离得太远，模组干脆就直线连接了，即使有一条贯通的路能连通」
        /// —— 玩家要的是「节点间无论距离多少都要尝试自动贴合」，并举例四分之一大弯路
        /// 首尾相切 45° 也要贴得下来。这里的关键是**比例是尺度无关的**：
        /// 一条 800 米的弯路和一条 80 米的同形状弯路，绕行比一模一样，
        /// 所以只要闸是纯比例，「距离」就根本进不了判据 —— 反馈 6 不需要绝对余量来解决。
        /// 四分之一矩形弯路：两条直角边各 L，弦 L√2 ⇒ 绕行比 1.414；
        /// 默认档 <c>Balanced</c> 的倍率是 3.0、<c>Shortest</c> 是 1.6（见 PolicyKit.ModeKnobs），
        /// 两种都放行，那个形状本来就一直在贴得下来的范围内。
        ///
        /// 那反馈 6 的症状从哪来的？是**端点**，不是绕行比：手动节点从此不许自动吸附 ⇒
        /// 它落在 <c>SnapKind.Free</c> ⇒ 老代码在「0b) 两端都要在路上」这道闸里整条退回直线
        /// ⇒ 玩家看到的是「明明有条贯通的路，模组偏要直线连」，而能贴着路走的形状根本没被搜索过。
        /// 这一档由接入点模型解决（见 <see cref="Attach"/> / <see cref="Lead"/>：端点不动，
        /// 走线自己长出贴合点），不要拿绕行预算去兜它。
        ///
        /// 中途加过的 <c>DETOUR_SLACK = 60</c>（比例与绝对余量取宽）已删：它只对**短弦**松绑，
        /// 而短弦在 3.0 倍率下本来就不会撞闸，结果只是让 20 米的两点被允许绕到 80 米 ——
        /// 那是玩家不想要的「绕一大圈」，而且把「距离越远越该老老实实沿路走」这条正确的直觉弄浑浊了。
        /// </summary>
        private static double DetourBudget(double straight, ResolvedTuning tuning)
        {
            double f = Math.Max(1.0, tuning.MaxDetourFactor);
            return straight * f;
        }

        /// <summary>
        /// A* 每次描边最多展开多少个接头。老值是 <c>MAX_GRAPH_NODES_PER_EDGE</c>=400 ——
        /// 那同时是「续接那条贪心走法」的步数上限，两个用途共用一个数：400 步对贪心够用，
        /// 对 A* 却会在大区域里先撞上限（撞了就是 NoPathWithinBudget ⇒ 直连，玩家看到的还是「太远不贴」）。
        /// 现在分开：贪心照旧 400，A* 给 1200。⚠ 这不是「越大越好」——每帧最多三遍侧向搜索 × 每条边，
        /// 上限抬太高会把 lastMs 顶上去；撞没撞上限实机看 <c>fb=NoPathWithinBudget</c> 的计数就知道。
        /// </summary>
        private const int A_STAR_EXPANSIONS = 1200;

        // ————————————————————————————— 节点续接（第五轮反馈 7）
        //
        // 【要解决的形状】两个手动节点之间**没有连续**的自动贴合路径 —— 最常见的是只有一端贴在路上，
        // 另一端点在空地上。上一版的处理是「整条退回玩家画的直线」（需求 3 那句「有一端不在路上就不贴」），
        // 代价是能贴的那半段也一起不贴了；再上一版在这里塞一段弧线，玩家说那不是他要的。
        // 现在按反馈 7：**能贴的那一段照样贴，剩下的用直线连过去**，这种连法出来的路径叫续接路径。
        //
        // 【三条硬要求，逐条对应代码】
        //  ① 「必须至少有 1 个节点处在道路/轨道/建筑边缘上」⇒ 两端都自由就直接返回 null（= 手动路径）。
        //  ② 「绕行距离依然受规则束缚」⇒ 每走一步都检查「已走长度 + 剩下到自由端的直线距离」
        //     不超过 <c>两点直线距离 × 绕行上限</c>；超了就在那里断开。
        //  ③ 「续接角度依然受规则束缚」⇒ 每个接头处的转角超过 <see cref="CONT_MAX_TURN_DEG"/> 就在那里断开
        //     （宁可在角上中断，也不要边界在路口拐进一条反方向的路）；并且只许**朝自由端前进**
        //     （距离没在减少 ⇒ 说明再走下去是在绕远，就地断）。
        //  断开之后如果贴出来的长度连一格都不到（<see cref="CONT_MIN_LENGTH"/>），这条边不值得续接，
        //  返回 null 让调用方回退手动路径 —— 断在半路又立刻接直线，玩家看到的是边界上无缘无故的小折角。
        private const double CONT_MAX_TURN_DEG = 75.0;
        private const double CONT_MIN_LENGTH = 6.0;

        /// <summary>
        /// 端点之一贴在网络上、另一端在空地上时，沿网络**尽可能贴过去**，剩下的留给直线。
        /// 返回 null 表示这条边不该续接（两端都不在网络上 / 一步都贴不出来 / 贴出来的东西被自交闸拦下）。
        /// </summary>
        private static TraceResult Continuation(PlacedNode a, PlacedNode b, WorldSnapshot world, ModConfig cfg,
                                                ResolvedTuning tuning, TraceContext ctx, double straight, bool edgeTier)
        {
            // 与主闸 0b) 同一个判据（StuckOnTarget）：Kind 对但坐标差着一截的锚点，
            // 在这里算「空地上那一端」—— 续接要从真正在路上那一端起。
            double tol = Math.Max(tuning.MinSpacing, 0.5);
            bool aOk = StuckOnTarget(a, world, edgeTier, tol);
            bool bOk = StuckOnTarget(b, world, edgeTier, tol);
            if (aOk == bOk) return null;          // 都不行 ⇒ 手动路径；都行 ⇒ 轮不到这里（正常描边那条路）

            PlacedNode from = aOk ? a : b;
            PlacedNode to = aOk ? b : a;          // from 是贴着路的那一端，to 是空地上那一端
            if (from.Edge < 0 || from.Edge >= world.Edges.Count) return null;
            GraphEdge first = world.Edges[from.Edge];
            if (first == null || first.Line == null || first.Line.Count < 2) return null;

            byte side = TraceSide(from, to);
            // 反馈 9/10：路缘档也不许沿**中心线**续接（与主描边那条闸同一口径）。
            // 续接的自由端是空地上的点（Side=CENTRE），所以只要贴着路的那一端不是路缘锚点
            //（例如游戏自己吸到的是路口中心），TraceSide 就会给出 CENTRE ⇒ 贴出来的那半段躺在马路中间。
            if (edgeTier && side == PlacedNode.SIDE_CENTRE)
            {
                side = SideTowardCentroid(first, ctx);
                if (side == PlacedNode.SIDE_CENTRE) return null;      // 判不出侧 ⇒ 不续接，整条留给玩家的直线
            }
            TraceResult r = new TraceResult();
            double budget = DetourBudget(straight, tuning);
            double walked = 0;                    // 已经贴出来的长度（纯几何，不含惩罚）

            // 走线从 from 所属边的某个接头出发：挑「离自由端更近」的那一头，另一头是绕远。
            int cur = CloserEnd(from, world, side, to.Pos);
            if (cur < 0 || cur >= world.Nodes.Count) return null;

            // 第一段：from 的锚点 → cur 接头（这一小段也是贴着路的，不能丢）。
            List<int> walkedEdges = new List<int>();
            walkedEdges.Add(from.Edge);
            P3 endA = a.Pos;
            P3 endB = b.Pos;
            {
                TraceResult head = new TraceResult();
                AppendAnchorSegmentForCont(head, from, world, cur, tuning, endA, endB, side);
                double headLen = head.Cost;
                double rest = to.Pos.DistanceTo(NodePos(world, cur));
                if (headLen + rest > budget) return null;
                r.Points.AddRange(head.Points);
                walked += headLen;
            }

            double remaining = to.Pos.DistanceTo(NodePos(world, cur));
            int prevNode = -1;
            int guard = 0;
            while (guard++ < ModConfig.MAX_GRAPH_NODES_PER_EDGE)
            {
                List<WorldSnapshot.AdjEntry> adj = world.Adjacency[cur];
                if (adj == null || adj.Count == 0) break;

                int bestNext = -1;
                int bestEdge = -1;
                double bestStep = double.MaxValue;
                double bestDist = double.MaxValue;
                for (int i = 0; i < adj.Count; i++)
                {
                    WorldSnapshot.AdjEntry en = adj[i];
                    if (en.EdgeIndex < 0 || en.EdgeIndex >= world.Edges.Count) continue;
                    if (walkedEdges.Contains(en.EdgeIndex)) continue;     // 不走回头路（同一条边来回 = 锯齿）
                    GraphEdge ge = world.Edges[en.EdgeIndex];
                    if (ge == null || ge.Line == null || ge.Line.Count < 2) continue;
                    // 与 A* 那一路同一份白名单（反馈 9）：续接是「能贴一段算一段」，
                    // 更不许走进步道/埠头/内部路 —— 那一段贴上了反而把断口两侧的形状带歪。
                    if (!PolicyKit.TraceEdgeAllowed(tuning.Tier, ge)) continue;

                    int nxt = en.OtherNode;
                    if (nxt < 0 || nxt >= world.Nodes.Count) continue;
                    if (prevNode >= 0 && nxt == prevNode) continue;
                    double step = EdgeArcLength(ge, en);
                    if (step <= 0) continue;
                    double dist = to.Pos.DistanceTo(NodePos(world, nxt));
                    // ③ 只许前进：这一跳之后离自由端必须更近（留 1 米余量，贴路时微小的远离是正常的）。
                    if (dist > remaining + 1.0) continue;
                    // ③ 转角闸：在 cur 这个接头上，进来的方向与出去的方向夹角太大就断开
                    //    （宁可在角上中断，也不要边界在路口拐进一条反方向的路）。
                    //    第一跳之前 prevNode 还是 -1 —— 进来的方向得用**锚点自己**，
                    //    否则最常见的形状（锚点在边中间、路口就在它前面）根本没人管角度。
                    if (TurnTooSharp(world, prevNode, cur, nxt, from.Pos)) continue;
                    // ② 绕行预算：已走 + 这一跳 + 剩下那段直线，不许超过预算。
                    if (walked + step + dist > budget) continue;
                    if (step < bestStep) { bestStep = step; bestNext = nxt; bestEdge = en.EdgeIndex; bestDist = dist; }
                }
                if (bestNext < 0) break;

                GraphEdge ge2 = world.Edges[bestEdge];
                TraceResult seg = new TraceResult();
                AppendEdgeSpan(seg, ge2, bestEdge, world, cur, bestNext, tuning, endA, endB,
                    SideForEdge(ge2, side, side, ctx), 0, 0);
                r.Points.AddRange(seg.Points);
                walkedEdges.Add(bestEdge);
                walked += bestStep;
                prevNode = cur;
                cur = bestNext;
                remaining = bestDist;

                // 交点必放节点（反馈 4）：接头就是「折线处」，钉住它，后面的简化不许把它抹掉。
                r.Pins.Add(NodePos(world, cur));
            }

            if (walked < CONT_MIN_LENGTH) return null;

            // 中断处那一格必须是**真的一格**，而且必须钉住。
            // 反馈 7 的原话是「在两个中断的节点之间用直线连接」，并且「续接的节点可以是手动节点，
            // 也可以是续接路径上的节点」—— 后一种就是这里：直线的那一端。
            // 上面循环里那次 Pins.Add 只在**真的跳过一跳**之后才登记，而最常见的形状是
            // 「锚点前面正好是个路口」：第一跳就被角度或预算闸拦下 ⇒ 钉数为 0，
            // 后面简化一使劲，那条直线就接到了一个已经不存在的点上（离线壳 T8 抓到）。
            P3 stopPos = NodePos(world, cur);
            bool hasStop = r.Points.Count > 0 && r.Points[r.Points.Count - 1].DistanceTo(stopPos) <= 1e-6;
            if (!hasStop && (r.Points.Count == 0 ||
                             r.Points[r.Points.Count - 1].DistanceTo(stopPos) >= tuning.MinSpacing))
            {
                r.Points.Add(stopPos);
            }
            r.Pins.Add(stopPos);

            // 顺序必须是 a → b：from 是 b 时整条反过来。
            if (!aOk) r.Points.Reverse();

            r.UsedNetwork = true;
            r.Continued = true;
            r.Cost = walked + remaining;          // 末段那条直线也算进绕行比：续接不许借直线抄近路
            unchecked
            {
                long sig = 1469598103934665603L;
                for (int i = 0; i < walkedEdges.Count; i++)
                {
                    int e = walkedEdges[i];
                    if (e < 0 || e >= world.Edges.Count) continue;
                    sig = (sig ^ world.Edges[e].Signature) * 1099511628211L;
                    r.UsedEdges.Add(e);
                }
                r.Signature = sig;
            }

            // 自交 / 退化 / 绕行比三道闸照常过一遍（续接不是免检通道）。
            TraceFallback why = Accept(r, a, b, world, cfg, ctx, tuning, straight);
            if (why != TraceFallback.None) return null;
            r.Continued = true;                   // Accept 里的 Finish 不碰这一格，但回退路径会清点，稳妥起见重申
            return r;
        }

        /// <summary>转角超过 <see cref="CONT_MAX_TURN_DEG"/> ⇒  True（续接就在这里断开，不再往前走）。</summary>
        private static bool TurnTooSharp(WorldSnapshot world, int prev, int node, int next)
        {
            double cos = TurnCosine(world, prev, node, next);
            double lim = Math.Cos(CONT_MAX_TURN_DEG * Math.PI / 180.0);
            return cos < lim;
        }

        /// <summary>
        /// 续接专用的转角闸：第一个接头之前没有「上一个图节点」（锚点在边的中间），
        /// 就用锚点自己当来向。少这一步，最常见的那种形状 —— 玩家点的地方前面就是一个路口 ——
        /// 恰好是角度规则唯一管不到的地方（反馈 7 要求续接「依然受规则束缚」）。
        /// </summary>
        private static bool TurnTooSharp(WorldSnapshot world, int prev, int node, int next, P3 incomingFrom)
        {
            if (prev < 0)
            {
                P3 np = NodePos(world, node);
                P3 xp = NodePos(world, next);
                V2 inDir = (V2.From(np) - V2.From(incomingFrom)).Normalized();
                V2 outDir = (V2.From(xp) - V2.From(np)).Normalized();
                if (inDir.Length <= 1e-9 || outDir.Length <= 1e-9) return false;
                return inDir.Dot(outDir) < Math.Cos(CONT_MAX_TURN_DEG * Math.PI / 180.0);
            }
            return TurnTooSharp(world, prev, node, next);
        }

        /// <summary>锚点所属边的两个接头里，离目标更近的那一个（锚点自己就是接头时用它自己）。</summary>
        private static int CloserEnd(PlacedNode p, WorldSnapshot world, byte side, P3 target)
        {
            if (p.Kind == SnapKind.NetNode && p.GraphNode >= 0 && p.GraphNode < world.Nodes.Count) return p.GraphNode;
            if (p.Edge < 0 || p.Edge >= world.Edges.Count) return -1;
            GraphEdge ge = world.Edges[p.Edge];
            if (ge == null) return -1;
            if (ge.StartNode == ge.EndNode) return ge.StartNode;
            double a = NodePos(world, ge.StartNode).DistanceTo(target);
            double b = NodePos(world, ge.EndNode).DistanceTo(target);
            if (double.IsPositiveInfinity(a)) return ge.EndNode;      // 那一头的节点没进快照
            if (double.IsPositiveInfinity(b)) return ge.StartNode;
            return a <= b ? ge.StartNode : ge.EndNode;
        }

        /// <summary>
        /// 接头坐标。**第八轮反馈 5 的落点就在这一句**：属于某路口的接头一律取那个路口的唯一中心点
        /// （分组与中心点算法见 Engine/JunctionKit.cs），不属于路口的取节点原位置。
        /// 走线上每个接头坐标都从这里出 ⇒ 同一路口不管从哪个车道方向连过来，经过的都是同一个点，
        /// 「在偏心的那个节点上拐弯」这件事在结构上不可能再发生。
        /// ⚠ 这一句不能写成 <c>NodePos(...)</c>：上面那条批量替换差点把它变成自我递归（栈溢出）。
        /// </summary>
        private static P3 NodePos(WorldSnapshot world, int i)
        {
            if (i < 0 || i >= world.Nodes.Count) return new P3(double.PositiveInfinity, double.PositiveInfinity, 0);
            return world.NodePoint(i);
        }

        /// <summary>续接专用的「锚点 → 起始接头」那一段：与 <see cref="AppendAnchorSegment"/> 同一口径，只是不剪角。</summary>
        private static void AppendAnchorSegmentForCont(TraceResult r, PlacedNode a, WorldSnapshot world, int startNode,
                                                       ResolvedTuning tuning, P3 endA, P3 endB, byte side)
        {
            if (a.Kind == SnapKind.NetNode && a.GraphNode == startNode) return;
            if (a.Edge < 0 || a.Edge >= world.Edges.Count) return;
            GraphEdge ge = world.Edges[a.Edge];
            Polyline line = ge.LineFor(side);
            if (line == null || line.Count < 2) return;
            double total = Total(ge, side);
            double arcNode = ge.StartNode == startNode ? 0 : total;
            double arcA = ArcOnLine(a, line, side, total);
            AppendSpan(r, line, arcA, arcNode, endA, endB, tuning);
            if (!r.UsedEdges.Contains(a.Edge)) r.UsedEdges.Add(a.Edge);
        }


        /// <summary>
        /// 两条候选里取「更外围」的那条。1.02 的门槛是为了确定性：一样外围时保留先来者，
        /// 否则浮点噪声会让路线在鼠标移动中来回翻（玩家看到「贴合的路径忽然换了一条」）。
        /// </summary>
        private static TraceResult BetterRoute(TraceResult cur, TraceResult cand, TraceContext ctx)
        {
            if (cand == null || cand.Fallback != TraceFallback.None) return cur;
            if (cur == null || cur.Fallback != TraceFallback.None) return cand;
            return MeanDistanceToCentroid(cand, ctx) > MeanDistanceToCentroid(cur, ctx) * 1.02 ? cand : cur;
        }

        /// <summary>
        /// 走线各中间点到区域质心的平均平面距离 —— 「更外围」这个说法的可量化形式：
        /// 中间那条小巷离街区中心近，外围那条马路离街区中心远，两者一比就分开。
        /// 没有中间点时返回 0（两条候选都一样，交给先来者，保证确定性）。
        /// </summary>
        private static double MeanDistanceToCentroid(TraceResult res, TraceContext ctx)
        {
            if (!ctx.HasCentroid || res == null || res.Points == null || res.Points.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < res.Points.Count; i++) sum += res.Points[i].DistanceTo(ctx.Centroid);
            return sum / res.Points.Count;
        }

        /// <summary>这一档的锚点算不算「贴在可以描边的东西上」（需求 3：两端有一个不在路上就不贴合）。</summary>
        private static bool IsTraceable(PlacedNode p, bool edgeTier)
        {
            switch (p.Kind)
            {
                case SnapKind.NetNode: return true;
                case SnapKind.NetCentre: return !edgeTier;
                case SnapKind.NetSide: return edgeTier;
                case SnapKind.ObjectSide: return edgeTier;
                case SnapKind.AreaBorderSame:
                case SnapKind.AreaBorderOther:
                case SnapKind.MapTileBorder: return true;
                default: return false;         // Free：玩家点的是空地，没有可描的东西
            }
        }

        /// <summary>
        /// 锚点**自己报的位置**离它声称贴着的几何有多远（米）。0 = 正在线上。
        /// 认不出来的（下标越界、Free、没有线）返回 0 —— 那种端点由 <see cref="IsTraceable"/> 那一层挡，
        /// 这里只回答「线还在，点却跑了」这一件事。
        ///
        /// 【为什么要有它】第八轮反馈 3/10 之后，玩家那一格的坐标永远不许被模组改，
        /// 而跟随/拖拽还是要把「重锚出来的 Edge/Arc/Side」带上（描边要用它选边、选侧）。
        /// 于是会出现「Kind 说贴在路缘、Pos 却在街区里」这种锚点：
        /// 光看 <see cref="IsTraceable"/> 会以为它已经在路上 ⇒ 不加接入点 ⇒
        /// 走线沿路走完最后一跳**凭空**斜着接到玩家那格 —— 那一跳既没有节点，
        /// 预览与落档也可能各算各的（反馈 4）。有了这个距离，端点该不该长出一个接入节点就只有一条口径。
        /// </summary>
        private static double AnchorGap(PlacedNode p, WorldSnapshot world)
        {
            if (p.Kind == SnapKind.Free || world == null) return 0;
            Polyline line = AnchorLine(p, world);
            if (line == null || line.Count < 2) return 0;
            P3 best;
            int segIdx;
            double segT, arc, dist;
            GeoKit.ClosestPointOnPolyline(line, p.Pos, out best, out segIdx, out segT, out arc, out dist);
            return dist;
        }

        /// <summary>锚点所属的那条折线（网络的按侧取缘线，边界/轮廓直接用自己的线）。</summary>
        private static Polyline AnchorLine(PlacedNode p, WorldSnapshot world)
        {
            if (AnchorCode.IsNet(p.Edge))
            {
                // 网络边的编码就是下标本身（Edge >= 0 ⇒ 第 Edge 条边，见 AnchorCode.IsNet）
                int e = (int)p.Edge;
                if (e < 0 || e >= world.Edges.Count) return null;
                GraphEdge ge = world.Edges[e];
                if (ge == null) return null;
                Polyline line = ge.LineFor(p.Side);
                return line != null ? line : ge.Line;
            }
            if (AnchorCode.IsBorder(p.Edge))
            {
                int b = -(p.Edge + 1);
                if (b < 0 || b >= world.Borders.Count) return null;
                return world.Borders[b].Line;
            }
            if (AnchorCode.IsObject(p.Edge))
            {
                int o = AnchorCode.Decode(p.Edge, AnchorCode.OBJECT_BASE);
                if (o < 0 || o >= world.Objects.Count) return null;
                return world.Objects[o].Line;
            }
            return null;
        }

        /// <summary>
        /// 这一端**现在就能描边**吗：档位认它，而且它报的坐标确实还在那条几何上
        /// （差得太远的 ⇒ 调用方去另找一个接入点，见 <see cref="AnchorGap"/>）。
        /// </summary>
        private static bool StuckOnTarget(PlacedNode p, WorldSnapshot world, bool edgeTier, double tol)
        {
            if (!IsTraceable(p, edgeTier)) return false;
            if (p.Kind == SnapKind.NetNode && world != null && p.GraphNode >= 0 && p.GraphNode < world.Nodes.Count)
            {
                // 路口锚点：游戏给每个节点只有一个坐标，路口合并后又是质心 —— 判它离那个交点多远。
                return p.Pos.DistanceTo(world.NodePoint(p.GraphNode)) <= tol;
            }
            return AnchorGap(p, world) <= tol;
        }

        /// <summary>
        /// 这条走线沿哪条折线：两端同侧 ⇒ 那一侧；只有一端有侧 ⇒ 跟着那一端；都没有 ⇒ 中心线档。
        /// 交叉口锚点（<see cref="SnapKind.NetNode"/>）本身没有侧，所以「一端是路口、一端是路缘」时
        /// 跟着有侧的那一端 —— 这正是需求 3 里最常见的形状：从路口顺着某条路的一侧缘走出去。
        /// </summary>
        private static byte TraceSide(PlacedNode a, PlacedNode b)
        {
            if (a.Side == b.Side) return a.Side;
            if (a.Kind == SnapKind.NetSide) return a.Side;
            if (b.Kind == SnapKind.NetSide) return b.Side;
            return PlacedNode.SIDE_CENTRE;
        }

        /// <summary>
        /// 区域质心在这条边的哪一侧 ⇒ 就贴哪一侧的外沿（反馈 9/10：路缘档不许沿中心线描边）。
        /// 与 SnapKit.OfferSide 的质心判侧同一口径（那边是给吸附候选打分，这边是两端都没带侧时兜底），
        /// 两处必须一致，否则「吸上来的点」与「描出来的线」会分居马路两侧。
        /// 两侧外沿都量不到（prefab 没给边线数据）时返回 SIDE_CENTRE，调用方退回续接/直线。
        /// </summary>
        private static byte SideTowardCentroid(GraphEdge ge, TraceContext ctx)
        {
            // TraceContext 是 struct（没有 null），HasCentroid 就是「有没有质心可用」那一道判据。
            if (ge == null || !ctx.HasCentroid) return PlacedNode.SIDE_CENTRE;
            double dl = GeoKit.DistanceFromPoint(ge.LeftLine, ctx.Centroid);
            double dr = GeoKit.DistanceFromPoint(ge.RightLine, ctx.Centroid);
            bool hasL = !double.IsPositiveInfinity(dl);
            bool hasR = !double.IsPositiveInfinity(dr);
            if (hasL && hasR) return dl <= dr ? PlacedNode.SIDE_LEFT : PlacedNode.SIDE_RIGHT;
            if (hasL) return PlacedNode.SIDE_LEFT;
            if (hasR) return PlacedNode.SIDE_RIGHT;
            return PlacedNode.SIDE_CENTRE;
        }

        // （已删除）WouldCross(PlacedNode, PlacedNode, WorldSnapshot)
        //
        // 它原来只做一件事：给上面那道「连线跨越 ⇒ 整段不贴合」的一票否决当判据
        //（含「两端同一条路分居两侧」那条特例）。第八轮反馈 9 把那道否决撤了 ⇒ 这个函数没有别的调用者。
        // 「分居同一条路两侧」这种形状并没有没人管：走线要沿两条缘线各走一趟再折回来，
        // 由 Rejects 里的自交闸与薄片退化闸拦（压不成薄片就不放行），严格档另外在 SnapKit.OfferSide
        // 那一层连候选都不递（SnapKit.CrossesObstacle 仍在那儿用着，删的是这边这层否决）。

        /// <summary>
        /// 把一条候选走线「定型 + 判优」：先简化到**真正要交给游戏的那条折线**，再在这条折线上做
        /// 绕行比 / 自交 / 退化判定。返回 None 表示可用（res 已定型）；否则清空 res 的点、
        /// 把 <see cref="TraceResult.Fallback"/> 置成具体原因，调用方直接回退成游戏直连。
        ///
        /// 判定放在简化之后有两个理由：
        ///  ① 玩家看到的就是简化后的形状 —— 拿原始密采样说事，可能出现「原始线自交、交付线并不自交」
        ///    这种把能用的边白白退回直线的误伤；反之也一样，自欺。
        ///  ② <see cref="SimplifyKit.SelfIntersects"/> 是 O(n²)。未简化的长走廊按 2 米采样能到上千点，
        ///    一次判定就几十毫秒（节点提交那一帧卡住）。定型后最多 MaxNodesPerEdge 个点，判定是常数级。
        /// </summary>
        private static TraceFallback Accept(TraceResult res, PlacedNode a, PlacedNode b, WorldSnapshot world, ModConfig cfg, TraceContext ctx, ResolvedTuning tuning, double straightLen)
        {
            Finish(res, a, b, world, cfg, tuning, straightLen);
            TraceFallback why = res.Fallback != TraceFallback.None ? res.Fallback : Rejects(res, a, b, world, ctx, tuning, straightLen);
            if (why != TraceFallback.None)
            {
                res.Points.Clear();
                res.Pins.Clear();
                res.UsedNetwork = false;
                res.UsedBorder = false;
                res.Fallback = why;
            }
            return why;
        }

        /// <summary>
        /// 结果能不能直接用。返回 None 表示可用，否则返回**具体**的回退原因
        /// （老实现返回 bool，于是「绕行太长」被贴成了 WouldSelfIntersect，
        /// DetourTooLong 这一档在整个文件里从没被赋过值 —— 调参时看日志会完全误判原因）。
        ///
        /// <paramref name="world"/> 只为一件事存在：<see cref="ResultCrosses"/>。
        /// 判的是**交付出去的那条折线自己**，不是玩家那两个端点之间的连线 ——
        /// 后者正是第八轮反馈 9 要撤掉的那道一票否决。
        /// </summary>
        private static TraceFallback Rejects(TraceResult res, PlacedNode a, PlacedNode b, WorldSnapshot world,
                                            TraceContext ctx, ResolvedTuning tuning, double straightLen)
        {
            if (res.Points.Count == 0) return TraceFallback.None;
            if (res.Cost > DetourBudget(straightLen, tuning)) return TraceFallback.DetourTooLong;

            List<P3> full = new List<P3>(res.Points.Count + 2);
            full.Add(a.Pos);
            full.AddRange(res.Points);
            full.Add(b.Pos);
            if (SimplifyKit.SelfIntersects(full)) return TraceFallback.WouldSelfIntersect;
            if (ResultCrosses(world, full, tuning)) return TraceFallback.CrossesObstacle;

            if (ctx.ExistingRing != null && ctx.ExistingRing.Count >= 3)
            {
                // 候选边界 = 「已有环里 a→b 那一格被本条走线替换掉」，不是 ExistingRing + full 首尾相接。
                // 后者等于在环上又补了一条 a→b 的弦 —— 而那条弦正是我们这次要换掉的东西：
                // 跟随已提交区域时（FollowKit）每一条边本来就躺在环上，于是八条边里五条被判成
                // WouldSelfIntersect（离线壳 X9 抓到）。找不到那一格时才退回老的拼接法（宁可多判一次自交，
                // 也不要放过真的把区域打成蝴蝶结的走线）。
                List<P3> ring = CandidateRing(ctx.ExistingRing, a.Pos, b.Pos, full, ctx.HasRingEdge ? ctx.RingEdge : -1);
                if (ring == null)
                {
                    ring = new List<P3>(ctx.ExistingRing.Count + full.Count);
                    ring.AddRange(ctx.ExistingRing);
                    ring.AddRange(full);
                }
                if (SimplifyKit.RingSelfIntersects(ring)) return TraceFallback.WouldSelfIntersect;
                // 退化面积下限：单位是 m²。25 m² 是「一块巴掌大的碎皮」的下限；
                // 再用区域尺度抬一下，避免大行政区用 4 m² 这种过松的判据放行长条薄片。
                double minArea = Math.Max(25.0, tuning.MinSpacing * tuning.SnapRadius * 0.02);
                if (SimplifyKit.IsDegenerateRing(ring, minArea)) return TraceFallback.WouldDegenerate;
            }
            return TraceFallback.None;
        }

        /// <summary>
        /// **交付折线自己**有没有横穿马路或建筑 —— 第八轮反馈 8 与 9 合起来的那道闸。
        ///
        /// 两道闸的区别就是这两条反馈的区别：
        ///  · 反馈 9：「两个节点的连线压到了东西（中间有别的区域），可贴合路径明明干干净净，却照样不让建」
        ///    ⇒ 判**玩家那两个端点之间的连线**是错的，所以原来那道 <c>WouldCross</c> 否决整个删掉了；
        ///  · 反馈 8：「产业区/表面区域的路径会贴到道路中间去」⇒ 判**要走线自己**不能横跨马路：
        ///    两端分别吸在同一条路的两条缘线上时，走线会从一条缘线直穿到另一条，把半条马路划进地块。
        ///    这一条恰好只有**路缘档**（专门产业区/垃圾场/表面规划/地皮）会碰到，因为中心线档本来就沿着中心线走、
        ///    游戏自己也让市辖区跨路（FACT：AreaType.District 的吸附只挑中心线，没有不许跨越这一说），
        ///    所以这道闸按 <see cref="PolicyKit.UsesEdgeTargets"/> 只对路缘档生效。
        ///
        /// 逐**段**判（不是只判首末），因为横跨就发生在中间某一段上；用的判据与吸附端是同一个
        /// <see cref="SnapKit.CrossesObstacle"/>（同一份白名单，见 PolicyKit.IsTargetNet 那段说明）。
        /// 成本：每段一次带包围盒粗筛的相交检测，段数由 MaxNodesPerEdge 封顶，且只在路缘档跑。
        /// </summary>
        private static bool ResultCrosses(WorldSnapshot world, List<P3> full, ResolvedTuning tuning)
        {
            if (world == null || full.Count < 2) return false;
            if (!PolicyKit.UsesEdgeTargets(tuning.Tier)) return false;
            for (int i = 0; i + 1 < full.Count; i++)
            {
                if (SnapKit.CrossesObstacle(world, full[i], full[i + 1])) return true;
            }
            return false;
        }

        /// <summary>两点是不是同一格（锚点位置是我们自己算出来的，比对用毫米级容差，不用语义容差）。</summary>
        private static bool SamePoint(P3 p, P3 q)
        {
            return p.DistanceTo(q) <= 1e-3;
        }

        /// <summary>
        /// 把 <paramref name="path"/>（a→…→b 的完整走线）插进已有环里，替换掉它对应的那一格。
        /// 优先按 <see cref="TraceContext.RingEdge"/> 定位；没有就按**两端位置**找那一格。
        /// 都找不到返回 null，调用方退回老的首尾相接（宁可多判一次自交，也不放过真的蝴蝶结）。
        /// </summary>
        private static List<P3> CandidateRing(IList<P3> ring, P3 a, P3 b, IList<P3> path, int ringEdge)
        {
            int n = ring.Count;
            if (n < 3 || path == null || path.Count < 2) return null;
            int i = -1;
            bool rev = false;
            if (ringEdge >= 0 && ringEdge < n)
            {
                i = ringEdge;
                int j = ringEdge + 1 == n ? 0 : ringEdge + 1;
                rev = SamePoint(ring[ringEdge], b) && SamePoint(ring[j], a);
            }
            else
            {
                for (int k = 0; k < n; k++)
                {
                    int j = k + 1 == n ? 0 : k + 1;
                    if (SamePoint(ring[k], a) && SamePoint(ring[j], b)) { i = k; rev = false; break; }
                    if (SamePoint(ring[k], b) && SamePoint(ring[j], a)) { i = k; rev = true; break; }
                }
            }
            if (i < 0) return null;

            List<P3> p = new List<P3>(path);
            if (rev) p.Reverse();                      // 环里这一格是 b→a 方向：走线倒过来才对得上
            List<P3> outp = new List<P3>(n + p.Count);
            for (int k = 0; k <= i; k++) outp.Add(ring[k]);
            for (int k = 1; k < p.Count; k++) outp.Add(p[k]);      // p[0] 与 ring[i] 重合，跳过
            for (int k = i + 2; k < n; k++) outp.Add(ring[k]);     // ring[i+1] 由走线末端顶上
            return outp;
        }

        private static void Finish(TraceResult res, PlacedNode a, PlacedNode b, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning, double straightLen)
        {
            // 第八轮反馈 5：走线里凡是正好站在某个路口**成员节点**上的顶点，先统一到那个路口的中心点。
            // 必须在简化与交点登记之前做：MarkCorners 按最终顶点顺序认「交点」，
            // 而简化闸要看到并完之后的真实形状（两个成员并到同一个中心 ⇒ 中间那一段本来就没长度，
            // 留着一个重合点只会让游戏那边的最小节点间距判据白吃一格）。
            ClampToJunctions(res, world);
            // 只有**网络**描边受路面宽度硬界约束：已有区域边界/建筑轮廓不是路面，
            // 按「半幅路宽」去卡它们等于凭空造出一条谁都没要求的密度线（回归壳 C9 第一次全绿是靠
            // tuning.ChordTol=0 的夹具，一旦把默认值填进 Resolve 就把这些档一起勒死了）。
            double hard = res.UsedNetwork ? ChordHardTolerance(res, world, cfg, tuning) : 0.0;

            // 反馈 4：「自动贴合路径上有折线处的地方叫交点 ⇒ 交点必须放置节点」。
            // 交点在这里登记（不是在采样时）：只有到这一步才知道最终走线的顶点顺序，
            // 而登记完之后所有简化闸都必须保住它们 —— 顺序反过来的话简化会先把角点抹掉，
            // 再按抹完的点判「这里没有交点」。
            if (res.UsedNetwork || res.UsedBorder) MarkCorners(res, tuning);

            // 第六轮反馈 1：「直线边上也在手动节点旁边多一个节点」。根因是 SimplifyKit 的三道闸
            // 无条件保住点列的**首格与末格**，而点列不含手动端点 ⇒ 直线边必然在离 a、b 各约 MinSpacing 处
            // 留下 1~2 个自动点。修法：送简化前把两个手动端点当**哨兵**插到首尾，让 keep[0]/keep[last]
            // 落在手动节点上；直线边经 RDP + MergeCollinear 后内部清空 ⇒ 0 个自动点。
            // 哨兵在简化后剥掉（它们不是自动点）；交点 pin 与精细度补点不受影响。
            bool sentinel = res.Points != null && res.Points.Count > 0;
            if (sentinel)
            {
                res.Points.Insert(0, a.Pos);
                res.Points.Add(b.Pos);
            }

            int budget = NodeBudget(res, tuning);
            double usedTol;
            res.Points = SimplifyKit.FitToBudget(DecimateForSimplify(res.Points, res.Pins), tuning.SimplifyTolerance,
                hard, tuning.MinSpacing, budget, res.Pins, out usedTol);
            if (sentinel)
            {
                if (res.Points.Count > 0 && res.Points[0].DistanceTo(a.Pos) <= 1e-6) res.Points.RemoveAt(0);
                if (res.Points.Count > 0 && res.Points[res.Points.Count - 1].DistanceTo(b.Pos) <= 1e-6)
                    res.Points.RemoveAt(res.Points.Count - 1);
            }
            // 反馈 7：与既有区域边界重叠的那一段，**照抄它的节点坐标**（不再自己算）。
            // 必须在简化之后跑：简化会按曲率删点/挪点，先抄再简化等于白抄。
            int cap = AdoptBorderNodes(res, world, tuning, budget);
            res.Corridor = new Polyline(res.Points == null ? new List<P3>() : res.Points).Bounds();
            res.Corridor = res.Corridor.Inflated(straightLen * 0.5 + 10.0);
            P3 boxA = new P3(a.Pos.X, a.Pos.Y, 0);
            P3 boxB = new P3(b.Pos.X, b.Pos.Y, 0);
            res.Corridor.Expand(boxA);
            res.Corridor.Expand(boxB);
            if (res.Points.Count > cap) res.Fallback = TraceFallback.NodeBudget;
        }

        /// <summary>
        /// 「多近算同一个节点位置」（米）。我们描出来的点与既有区域的节点都躺在**同一条路缘线上**，
        /// 横向偏差是厘米级，差别只在沿路方向上差了多少 —— 2 米足够认出「这是同一格」，
        /// 又不至于把隔壁路口的节点拽过来。
        /// </summary>
        private const double ADOPT_TOL = 2.0;

        /// <summary>
        /// cos(30°)：方向差超过 30° 的不算「重叠」，那是横穿过去的另一块地
        /// （十字路口的另一条街上正好有个节点离得很近时，靠这一条挡住）。
        /// </summary>
        private const double ADOPT_PARALLEL_COS = 0.866;

        /// <summary>一条边界最多替我们补多少个中间节点（防御：别人家的密点环不许把我们的预算吃光）。</summary>
        private const int ADOPT_MAX_SPAN = 24;

        /// <summary>
        /// 与既有区域边界共线的那一段，直接采用**它们的节点坐标**（第六轮反馈 7）。
        ///
        /// 玩家的原话：「如果两个节点的自动贴合路径有部分和另一个区域的边缘在同一段道路/轨道上，
        /// 那这部分自动放置的节点跟那个区域的节点一致，重叠不赋分不要去判断曲率什么的计算节点位置了。」
        ///
        /// 为什么要"完全一致"而不是"差不多"：两块地共用一段边界时，坐标差几厘米就是**两条不重合的边**，
        /// 游戏放置检查会把它判成物体互相碰撞（玩家看到的形状是"这块地放不下去"）；
        /// 坐标逐位相同时游戏才认得出这是同一条边。所以这里做的是坐标**替换**，不是加权平均，
        /// 也不再跑曲率/简化那一套（那正是"赋分"）。
        ///
        /// 两步：
        ///  ① 我们每个自动点，如果在某条既有边界上找到 <see cref="ADOPT_TOL"/> 以内、且方向大致平行的
        ///     **顶点**，就把坐标换成那个顶点（逐位相同）；
        ///  ② 相邻两个自动点如果认到的是**同一条边界**，把它俩之间那些我们没描出来的顶点也补进来 ——
        ///     否则两块地虽然端点相同、中间却一边有点一边没点，仍然不是同一条边。
        ///
        /// 只在**沿网络描边**（<see cref="TraceResult.UsedNetwork"/>）时跑：
        /// 走 2)/2b) 那两条分支时点列本来就是从边界/轮廓上切下来的子段，坐标已经是逐位相同的了。
        ///
        /// 返回本次交付允许的点数上限：补了别人的顶点就得放宽预算，否则
        /// 「点数超预算 ⇒ 整条退回直线」会把这一段变成斜线，比节点不重合更糟。
        /// </summary>
        private static int AdoptBorderNodes(TraceResult res, WorldSnapshot world, ResolvedTuning tuning, int budget)
        {
            int cap = budget;
            if (res == null || res.Points == null || res.Points.Count == 0) return cap;
            if (!res.UsedNetwork) return cap;
            if (world == null || world.Borders.Count == 0) return cap;

            double tol = Math.Max(ADOPT_TOL, tuning.SimplifyTolerance * 2.0);
            List<P3> pts = res.Points;
            int adopted = 0;
            int[] borderOf = new int[pts.Count];
            int[] vertexOf = new int[pts.Count];
            for (int i = 0; i < pts.Count; i++) { borderOf[i] = -1; vertexOf[i] = -1; }

            // ① 逐点认亲：最近的那个边界顶点（还要方向大致平行）
            for (int i = 0; i < pts.Count; i++)
            {
                double bestD = tol;
                int bestB = -1;
                int bestV = -1;
                for (int bIdx = 0; bIdx < world.Borders.Count; bIdx++)
                {
                    BorderRef br = world.Borders[bIdx];
                    if (br == null || br.Line == null || br.Line.Count < 2) continue;
                    if (br.Line.Bounds().DistanceTo(pts[i]) > tol) continue;
                    for (int v = 0; v < br.Line.Count; v++)
                    {
                        P3 cand = br.Line.Points[v];
                        double d = cand.DistanceTo(pts[i]);
                        if (d >= bestD) continue;
                        if (!ParallelEnough(pts, i, br.Line.Points, v)) continue;
                        bestD = d;
                        bestB = bIdx;
                        bestV = v;
                    }
                }
                if (bestB < 0) continue;
                borderOf[i] = bestB;
                vertexOf[i] = bestV;
                if (pts[i].DistanceTo(world.Borders[bestB].Line.Points[bestV]) > 0) adopted++;
                pts[i] = world.Borders[bestB].Line.Points[bestV];      // 坐标**替换**，不是插值
            }

            // ② 相邻两点认到同一条边界 ⇒ 把它们之间的顶点也补上（端点相同、中间不同 ⇒ 仍然不是同一条边）
            List<P3> grown = new List<P3>(pts.Count + ADOPT_MAX_SPAN);
            bool inserted = false;
            for (int i = 0; i < pts.Count; i++)
            {
                grown.Add(pts[i]);
                if (i + 1 >= pts.Count) continue;
                if (borderOf[i] < 0 || borderOf[i] != borderOf[i + 1]) continue;
                Polyline line = world.Borders[borderOf[i]].Line;
                int from = vertexOf[i];
                int to = vertexOf[i + 1];
                int span = Math.Abs(to - from) - 1;
                if (span <= 0 || span > ADOPT_MAX_SPAN) continue;
                int stepDir = to > from ? 1 : -1;
                for (int k = from + stepDir; k != to; k += stepDir)
                {
                    P3 mid = line.Points[k];
                    // 与刚放进去的那个点重合就不重复插（既有边界自己可能有重复顶点）
                    if (grown[grown.Count - 1].DistanceTo(mid) <= 1e-6) continue;
                    grown.Add(mid);
                    inserted = true;
                    adopted++;
                }
            }
            if (inserted)
            {
                res.Points = grown;
                // 补进来的都是别人家**已经存在**的节点，不是我们新造的密度：预算按「原预算 + 补进来的个数」放宽，
                // 再给一点余量，免得「超预算 ⇒ 退回直线」把共线那段变成斜线。
                cap = Math.Max(budget, grown.Count);
            }
            res.Adopted = adopted;
            return cap;
        }

        /// <summary>
        /// 「我们这一格与那条边界的这一格是不是躺在**同一段路**上」：两边各自的进/出两段方向里，
        /// 只要有一对大致平行（夹角 ≤30°）就算。
        ///
        /// 为什么比两段而不是比"局部方向"：最该认亲的恰恰是**拐角上的节点**，而拐角处
        /// 「前一点指向后一点」那条平均方向是 45° 斜的，跟谁都不平行 —— 用平均方向判，
        /// 直段上的点认得上、角点认不上，正好把最要紧的那一类漏掉。
        /// 十字路口对面那条街上的节点仍然会被挡住：它的两段都与我们的两段垂直。
        /// </summary>
        private static bool ParallelEnough(List<P3> mine, int i, List<P3> theirs, int v)
        {
            V2 a1, a2, b1, b2;
            SegDirs(mine, i, out a1, out a2);
            SegDirs(theirs, v, out b1, out b2);
            return Math.Abs(a1.Dot(b1)) >= ADOPT_PARALLEL_COS
                || Math.Abs(a1.Dot(b2)) >= ADOPT_PARALLEL_COS
                || Math.Abs(a2.Dot(b1)) >= ADOPT_PARALLEL_COS
                || Math.Abs(a2.Dot(b2)) >= ADOPT_PARALLEL_COS;
        }

        /// <summary>
        /// i 处**进入**与**离开**那两段的单位方向。端点只有一侧有段 ⇒ 另一侧填成同一条
        /// （留零向量的话点积恒为 0，端点上的节点会被判成"不平行"而漏掉）。
        /// </summary>
        private static void SegDirs(List<P3> pts, int i, out V2 back, out V2 fwd)
        {
            back = new V2(0, 0);
            fwd = new V2(0, 0);
            if (pts == null || i < 0 || i >= pts.Count) return;
            if (i > 0) back = (V2.From(pts[i]) - V2.From(pts[i - 1])).Normalized();
            if (i + 1 < pts.Count) fwd = (V2.From(pts[i + 1]) - V2.From(pts[i])).Normalized();
            if (back.X == 0 && back.Y == 0) back = fwd;
            if (fwd.X == 0 && fwd.Y == 0) fwd = back;
        }

        /// <summary>
        /// 把走线上的**交点**登记成钉（反馈 4 的定义：直线与曲线交汇处、曲率发生变化的地方，以及拐过弯的接头）。
        ///
        /// 判据是相邻两段的方向差：转角 ≥ <see cref="ResolvedTuning.CornerDegrees"/> 就是折线处。
        /// 曲率变化不要求方向立刻改变 —— 一条直路接到一条缓弯上，头几米的方向差很小，但它是
        /// 「直线与曲线交汇处」，采样点列上表现为「连着好几格都在同方向微微偏」：
        /// 所以对「连续同向的偏转」做**累计**，累计量越过阈值时把这一段的第一格也钉住。
        /// 阈值由曲线精细度换算（精细度 0 ⇒ 只钉明显的角；精细度 100% ⇒ 连缓弯的分段也钉）。
        /// </summary>
        private static void MarkCorners(TraceResult res, ResolvedTuning tuning)
        {
            List<P3> pts = res.Points;
            if (pts == null || pts.Count < 3) return;
            double lim = tuning.CornerDegrees <= 0 ? 180.0 : tuning.CornerDegrees;
            double limCos = Math.Cos(lim * Math.PI / 180.0);
            double runCos = 1.0;
            int runStart = -1;
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                V2 inDir = (V2.From(pts[i]) - V2.From(pts[i - 1])).Normalized();
                V2 outDir = (V2.From(pts[i + 1]) - V2.From(pts[i])).Normalized();
                if (inDir.Length <= 1e-9 || outDir.Length <= 1e-9) continue;
                double cos = inDir.Dot(outDir);
                if (cos <= limCos)
                {
                    res.Pins.Add(pts[i]);            // 单格就拐过阈值：这就是一个角
                    runStart = -1;
                    runCos = 1.0;
                    continue;
                }
                // 缓弯：每格都不过阈值，但累计转角过了 ⇒ 把这一段的起点钉住（曲线与直线的交汇处）。
                if (runStart < 0) { runStart = i; runCos = cos; }
                else runCos *= cos;                  // 同向连续偏转：乘积近似等于累计夹角的 cos
                if (runStart >= 0 && runCos <= limCos)
                {
                    res.Pins.Add(pts[runStart]);
                    res.Pins.Add(pts[i]);
                    runStart = -1;
                    runCos = 1.0;
                }
            }
        }

        /// <summary>
        /// 弦高硬界：这条走线用到的**每一条**边都要满足，所以取各边容差的最小值 ——
        /// 一条走线里既有 25 米主干道又有 6 米小巷时，按小巷那条算密度，否则主干道上切出去的空隙
        /// 会连带把小巷那段的边界也画歪（需求 2 的「相邻两点连线不要超出道路边缘」是逐条路的）。
        /// </summary>
        private static double ChordHardTolerance(TraceResult res, WorldSnapshot world, ModConfig cfg, ResolvedTuning tuning)
        {
            // ChordTol<=0 只可能是「有人手搭了 ResolvedTuning 而没填这一格」（离线夹具就是这种）。
            // 当成「不封顶」处理会让密度保证在测试壳里静默消失 —— 退回到类别带宽上界，
            // 宁可多铺点也不要把需求 2 的「弦不许越出道路边缘」测不到。
            double hard = tuning.ChordTol > 0 ? tuning.ChordTol : PolicyKit.ChordToleranceBand(tuning.Tier, tuning.CurveDetail);
            for (int i = 0; i < res.UsedEdges.Count; i++)
            {
                int e = res.UsedEdges[i];
                if (e < 0 || e >= world.Edges.Count) continue;
                GraphEdge ge = world.Edges[e];
                if (ge == null) continue;
                double t = PolicyKit.ChordTolerance(cfg, tuning.Tier, ge.RoadWidth, tuning.CurveDetail);
                if (t < hard) hard = t;
            }
            return hard;
        }

        /// <summary>
        /// 节点预算。反馈 7 要求「单边节点上限设成无限」⇒ 玩家侧不再有这一档，
        /// 这里只剩一道**存档侧的硬保险**（<see cref="ModConfig.HARD_NODE_CAP"/>）：
        /// 一条边走出几千个点意味着走廊或选路已经不对了，与其把这么大的点表塞进区域
        /// （游戏的三角化会被它拖死），不如这一条边退回直线。
        /// 整条走线跨几条边就按几条边的额度给（老写法把单边额度当整线额度用，长走线会被逼着放粗容差）。
        /// </summary>
        private static int NodeBudget(TraceResult res, ResolvedTuning tuning)
        {
            int per = Math.Max(4, tuning.MaxNodesPerEdge);
            int spans = Math.Max(1, res.UsedEdges.Count);
            long budget = (long)per * spans;
            if (budget > ModConfig.HARD_NODE_CAP) budget = ModConfig.HARD_NODE_CAP;
            return (int)budget;
        }

        /// <summary>
        /// 送进简化管线之前的点数上限。Douglas–Peucker 最坏 O(n²)，而一条描边的原始点列只受
        /// 「绕行预算 × 游戏侧采样步长」约束（两端点 3 公里、2 米采样就是上千点起步），必须有个闸。
        /// 等间隔抽稀只可能砍掉比容差还细的抖动；真正交出去的形状由 <c>MaxNodesPerEdge</c> 决定，
        /// 所以这道闸不改结果，只把最坏耗时压回常数级（节点提交那一帧不能卡）。
        /// </summary>
        private const int RAW_CAP = 512;

        private static List<P3> DecimateForSimplify(List<P3> pts, List<P3> pins)
        {
            if (pts == null) return new List<P3>();
            if (pts.Count <= RAW_CAP) return pts;
            int stride = (pts.Count + RAW_CAP - 1) / RAW_CAP;
            List<P3> kept = new List<P3>(RAW_CAP + 2);
            for (int i = 0; i < pts.Count; i++)
            {
                // 抽稀这一道也要认钉：等间隔跳点会把「类型变化处那个必须留的交叉口」正好跳掉
                // —— 抽稀丢一个点，后面三道闸再怎么保护也救不回来。
                if ((i % stride) != 0 && !IsPin(pts[i], pins)) continue;
                if (kept.Count > 0 && kept[kept.Count - 1].DistanceSquaredTo(pts[i]) <= 1e-18) continue;
                kept.Add(pts[i]);
            }
            P3 last = pts[pts.Count - 1];
            // 末点是这条边接向下一个手动节点的出口，抽稀步长不整除时也要站住。
            if (kept[kept.Count - 1].DistanceTo(last) > 1e-12) kept.Add(last);
            return kept;
        }

        /// <summary>
        /// 走线里那些「正好站在某个路口成员节点上」的顶点，统一到那个路口的中心点（第八轮反馈 5）。
        ///
        /// 【为什么单独走一遍】拼接出来的走线，顶点是从各条边自己的折线里采出来的 ——
        /// 而游戏把一条路的缘线/中线一路采到**它自己那个节点**上（FACT：Game.Prefabs/NetGeometryData.cs:31/:38
        /// 那两个节点偏移量、以及 Game.Net/Curve 的 node→node 口径）。一个路口在我们的快照里
        /// 可能有 2~5 个成员节点（主网络 + 副网络/人行道 + 重叠的 SubNet），于是"经过路口"的那一格
        /// 到底落在哪个坐标，完全取决于当时走的是哪条边的哪一端 —— 这正是玩家截图里
        /// 「贴合路径在路口选了偏心的节点拐弯」的来历（中心点偏 8 米，肉眼就能看出一个台阶）。
        ///
        /// 【判据为什么是"这个顶点就是某个节点的位置"】只有路口才会让一个顶点**恰好**等于某个图节点坐标；
        /// 长边中间的采样点是折线自己的顶点，与任何节点都差着一截。所以这里按坐标相等认（毫米级容差），
        /// 而不是按"离路口多近"认 —— 后者会把弯道最后那一格无辜地拽到中心上去。
        /// </summary>
        private static void ClampToJunctions(TraceResult res, WorldSnapshot world)
        {
            if (res == null || res.Points == null || res.Points.Count == 0) return;
            if (world == null) return;
            List<Junction> js = world.Junctions;
            if (js == null || js.Count == 0) return;

            for (int i = 0; i < res.Points.Count; i++)
            {
                P3 p = res.Points[i];
                for (int k = 0; k < js.Count; k++)
                {
                    Junction j = js[k];
                    double dx = p.X - j.Center.X, dy = p.Y - j.Center.Y, dz = p.H - j.Center.H;
                    if (dx * dx + dy * dy + dz * dz <= 1e-12) break;         // 已经在中心点上
                    for (int m = 0; m < j.Nodes.Count; m++)
                    {
                        int ni = j.Nodes[m];
                        if (ni < 0 || ni >= world.Nodes.Count) continue;
                        GraphNode gn = world.Nodes[ni];
                        if (gn == null) continue;
                        double ex = p.X - gn.Pos.X, ey = p.Y - gn.Pos.Y, ez = p.H - gn.Pos.H;
                        if (ex * ex + ey * ey + ez * ez > 1e-12) continue;   // 这个顶点不是那一格
                        res.Points[i] = j.Center;
                        res.Clamped++;                                      // 统计行的 junc=（反馈 5 的机器凭据）
                        break;
                    }
                }
            }

            // 并到同一个中心之后可能出现连续重合点（同一路口的两个成员都被搬过来）：
            // 留着一个，游戏那边的最小节点间距就白吃一格，还可能被判成退化碎片。
            for (int i = res.Points.Count - 1; i > 0; i--)
            {
                if (res.Points[i].DistanceSquaredTo(res.Points[i - 1]) <= 1e-12) res.Points.RemoveAt(i);
            }
        }

        /// <summary>与 <c>SimplifyKit.IsPinned</c> 同一口径（1 微米内算同一个点）。</summary>
        private static bool IsPin(P3 p, List<P3> pins)
        {
            if (pins == null || pins.Count == 0) return false;
            for (int i = 0; i < pins.Count; i++) if (pins[i].NearlyEquals(p, 1e-6)) return true;
            return false;
        }

        /// <summary>沿一条折线取 a→b 之间的中段（不含两端点），追加到结果里。</summary>
        private static void FillFromPolyline(TraceResult res, Polyline line, double arcA, double arcB, P3 posA, P3 posB, ResolvedTuning tuning, bool keepElevation)
        {
            if (line == null || line.Count < 2) return;
            double lo = Math.Min(arcA, arcB);
            double hi = Math.Max(arcA, arcB);
            Polyline span = GeoKit.Subspan(line, lo, hi);
            if (span.Count < 2) return;
            List<P3> mid = new List<P3>();
            for (int i = 0; i < span.Count; i++)
            {
                P3 p = span.Points[i];
                if (p.DistanceTo(posA) < tuning.MinSpacing) continue;
                if (p.DistanceTo(posB) < tuning.MinSpacing) continue;
                mid.Add(p);
            }
            // 弧长方向可能反了（a 在 b 之后）：反向时把中间点倒过来，保证投影顺序与走线方向一致。
            if (arcB < arcA) mid.Reverse();
            res.Points.AddRange(mid);
            res.Cost = span.Length2D();
        }

        /// <summary>
        /// 走廊内局部图 A*。
        /// 为什么是局部图而不是全城图：全城建图要遍历几万条边（Playbook 记录过「全城 ToEntityArray
        /// + Dependency.Complete 才是大城市的卡点」），而两点之间真正用得上的只有走廊附近那一小片。
        /// 世界快照本来就是游戏侧按四叉树（FACT：Game.Net.SearchSystem.GetNetSearchTree，SearchSystem.cs:829）
        /// 在两点扩出的走廊里抓出来的，图只包含这一片 —— 搜索规模天然有界。
        ///
        /// <paramref name="sideFilter"/>：0 = 不限；+1 = 只走「有向弦 A→B 左手侧」的边；-1 = 只走右手侧。
        /// 这是需求 2「按最外围的路段贴合」的候选生成器（见 <see cref="Trace"/> 里的三遍搜索）。
        /// </summary>
        private static TraceResult ViaGraph(PlacedNode a, PlacedNode b, WorldSnapshot world, ModConfig cfg,
                                            ResolvedTuning tuning, int blockedEdge, byte side, TraceContext ctx, int sideFilter)
        {
            TraceResult r = new TraceResult();
            double straight = a.Pos.DistanceTo(b.Pos);
            V2 chordDir = (V2.From(b.Pos) - V2.From(a.Pos)).Normalized();
            double maxCost = DetourBudget(straight, tuning);
            int nodeCap = A_STAR_EXPANSIONS;

            // 【为什么两端都要能进出】
            // 老实现只从「离锚点近的那个接头」进图、到「离目标近的那个接头」出图。
            // 于是 a、b 前后排在同一条路上时，真正的最短路（沿所属边走到**远**端的接头）根本搜不到：
            // 回归壳 T2b 的真实最短路 160 m，老实现绕出 240 m。现在把 a 边的两个接头都作为源、
            // b 边的两个接头都作为汇，接入代价 = |锚点弧长 - 该接头弧长|，由搜索自己挑方向。
            int[] srcNodes = AnchorEnds(a, world);
            double[] srcCost = AnchorEndCosts(a, world, srcNodes, side);
            int[] dstNodes = AnchorEnds(b, world);
            double[] dstCost = AnchorEndCosts(b, world, dstNodes, side);
            if (srcNodes.Length == 0 || dstNodes.Length == 0) { r.Fallback = TraceFallback.NotOnNetwork; return r; }
            // 只有「两个锚点都是同一个交叉口」才算退化（源=汇、零长路径）。
            // 两条边共享一个接头**不是**退化：L 形路口 a 在底边、b 在竖边，
            // 正当走线就是 a→拐角→b，一刀切拦掉会让这类描边全变直线。
            if (a.Kind == SnapKind.NetNode && b.Kind == SnapKind.NetNode
                && a.GraphNode == b.GraphNode && a.GraphNode >= 0)
            {
                r.Fallback = TraceFallback.SameAnchor;
                return r;
            }
            int targetOfHeuristic = dstNodes[0];

            int n = world.Nodes.Count;
            double[] dist = new double[n];
            // 平行于 dist 的**纯几何弧长**累加：dist 里含换类/转角惩罚，用来排序；
            // Cost 必须是真实长度，否则「绕行上限」会被惩罚虚高（回归壳 T2b：真值 160 报成 163.5），
            // 惩罚越重越容易被误判成「绕太远 ⇒ 回退直线」。
            double[] geom = new double[n];
            int[] prevNode = new int[n];
            int[] prevEdge = new int[n];
            bool[] done = new bool[n];
            for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; geom[i] = double.PositiveInfinity; prevNode[i] = -1; prevEdge[i] = -1; }

            // 多源播种：同一个接头如果被两条源边都够到，取更小的接入代价。
            MinHeap heap = new MinHeap(256);
            for (int i = 0; i < srcNodes.Length; i++)
            {
                int s = srcNodes[i];
                if (s < 0 || s >= n) continue;
                if (srcCost[i] < dist[s] - 1e-9)
                {
                    dist[s] = srcCost[i];
                    geom[s] = srcCost[i];
                    heap.Push(s, dist[s] + Heuristic(world, s, targetOfHeuristic));
                }
            }

            // 多汇：哪个汇的「g + 出图代价」最小就取它；堆顶的 f 超过它即可停。
            int endNode = -1;
            double endExit = double.PositiveInfinity;
            double bestTotal = double.PositiveInfinity;
            double minExit = double.PositiveInfinity;
            for (int i = 0; i < dstCost.Length; i++) if (dstCost[i] < minExit) minExit = dstCost[i];
            int expanded = 0;
            bool found = false;

            while (heap.Count > 0)
            {
                int u;
                double fKey;
                heap.Pop(out u, out fKey);
                if (done[u]) continue;
                done[u] = true;
                double d = dist[u];
                // ⚠ 堆里存的是 f = g + h（A* 的排序键），**不是** g。老实现直接把弹出来的 f 当 g 用：
                // `through = d + step` ⇒ 每一跳都把父节点的启发项烙进 dist[]，代价随跳数复利膨胀
                // （回归壳 T2b/T3：两跳以上的描边全部静默退化成直线 —— 需求 2 的核心场景）。
                // 修正：弹出后一律回读 dist[u] 当 g。
                if (found && fKey >= bestTotal - 1e-12) break;   // 堆里不可能有更优的汇了
                for (int t = 0; t < dstNodes.Length; t++)
                {
                    if (dstNodes[t] != u) continue;
                    double total = d + dstCost[t];
                    if (total < bestTotal - 1e-12)
                    {
                        bestTotal = total;
                        endNode = u;
                        endExit = dstCost[t];
                        found = true;
                    }
                }
                if (++expanded > nodeCap) { r.Fallback = TraceFallback.NoPathWithinBudget; return r; }

                List<WorldSnapshot.AdjEntry> adj = world.Adjacency[u];
                for (int i = 0; i < adj.Count; i++)
                {
                    WorldSnapshot.AdjEntry en = adj[i];
                    if (en.EdgeIndex == blockedEdge) continue;
                    GraphEdge ge = world.Edges[en.EdgeIndex];
                    if (ge == null || ge.Line == null || ge.Line.Count < 2) continue;
                    // 白名单（反馈 9）：步道/水道/埠头/建筑内部路不许借道；路缘档还多一条隧道禁令
                    //（游戏贴路缘时就把 Tunnel 组合排除了，FACT：AreaToolSystem.cs:501-508；
                    //  描边绕过去的话边界会从地下穿一段）。高架不受此限（第五轮反馈 4 要它也能贴）。
                    if (!PolicyKit.TraceEdgeAllowed(tuning.Tier, ge)) continue;
                    if (!tuning.Knobs.AllowNetworkSwitch && KindChanged(world.Edges[a.Edge], ge)) continue;
                    double step = EdgeArcLength(ge, en);
                    double kindPenalty = 0;
                    if (prevEdge[u] >= 0)
                    {
                        GraphEdge pe = world.Edges[prevEdge[u]];
                        if (pe != null && KindChanged(pe, ge)) kindPenalty += tuning.Knobs.KindChangePenalty;
                        // 转角越大越贵：(1 - cosθ) ∈ [0,2]。没有这一项时描边会在路口来回折返，
                        // 产生「边界自交」——正是原模组 0.2.3 修的那类 bug。系数由贴合模式给（反馈 5）。
                        int pp = prevNode[u];
                        if (pp >= 0)
                        {
                            double cos = TurnCosine(world, pp, u, en.OtherNode);
                            kindPenalty += tuning.Knobs.TurnPenalty * (1.0 - cos) * Math.Max(1.0, step * 0.1);
                        }
                    }
                    if (sideFilter != 0 && !OnSideOfChord(world, u, en.OtherNode, a.Pos, chordDir, sideFilter)) continue;
                    // 反馈 8：叠置路网（高架压在地面路上、或一侧缘线共线）两条路线的长度**完全相同**，
                    // 谁胜出就由堆的出队顺序决定。加一点点与高度成正比的代价，让低的那条严格更便宜 ——
                    // 权重量级见 PolicyKit.HEIGHT_TIE_WEIGHT（10 米高架只贵 0.01 米，动不了真实的路线选择）。
                    // 注意这一项**只进代价、不进 geom**：geom 是交付给下游的真实几何长度，不许被策略污染。
                    double through = d + step + kindPenalty + PolicyKit.HeightTieCost(ge);
                    if (through > maxCost + minExit) continue;   // 连最省的出口都超预算 ⇒ 这条分支不必再展
                    int v = en.OtherNode;
                    if (v < 0 || v >= n) continue;
                    if (through < dist[v] - 1e-9)
                    {
                        dist[v] = through;
                        geom[v] = geom[u] + step;
                        prevNode[v] = u;
                        prevEdge[v] = en.EdgeIndex;
                        heap.Push(v, through + Heuristic(world, v, targetOfHeuristic));
                    }
                }
            }

            if (!found) { r.Fallback = TraceFallback.NoPathWithinBudget; return r; }

            // 回溯：节点序列 → 边序列 → 逐边取子段拼接。
            List<int> nodes = new List<int>();
            List<int> edges = new List<int>();
            int cur = endNode;
            while (cur >= 0)
            {
                nodes.Add(cur);
                int pe = prevEdge[cur];
                if (pe >= 0) edges.Add(pe);
                cur = prevNode[cur];
            }
            nodes.Reverse();
            edges.Reverse();

            double cost = double.IsPositiveInfinity(geom[endNode]) ? dist[endNode] : geom[endNode];
            // 逐段过滤用的是**本条描边的两个端点**（a.Pos / b.Pos），不是各小段自己的端点，见 AppendSpan 注释。
            P3 endA = a.Pos;
            P3 endB = b.Pos;
            int firstNode = nodes[0];
            int lastNode = nodes[nodes.Count - 1];
            // 每条边各自用哪条线（见 SideForEdge：绕街区一圈时「左/右」会逐边翻转，共用一个会把边界甩到马路对面）。
            // 先定侧，再接入段/剪角/钉节点都按这一条线来量，四处口径必须一致。
            byte sideA = SideForEdge(EdgeAt(world, a.Edge), a.Side, side, ctx);
            byte sideB = SideForEdge(EdgeAt(world, b.Edge), b.Side, side, ctx);
            bool aSpan = AnchorSpanWalked(a, world, firstNode, sideA);
            bool bSpan = AnchorSpanWalked(b, world, lastNode, sideB);

            // 路口转弯处两侧的缘线会各自伸到路口中心、互相穿过 ⇒ 直接拼出来的走线在每个角上打了个 X，
            // 自交闸把整条边判死 ⇒ 玩家看到「绕街区一圈，每个转角都退回直线」（离线壳 X9 八条边里两条被拒）。
            // trimAt[k] = 在第 k 个接头上要从两侧各剪掉多少米（剪完正好接上 ⇒ 角上是一条贴路口的斜切）。
            double[] trimAt = new double[nodes.Count];
            for (int k = 0; k < nodes.Count; k++)
            {
                int inEdge = k == 0 ? (aSpan ? a.Edge : -1) : edges[k - 1];
                int outEdge = k == nodes.Count - 1 ? (bSpan ? b.Edge : -1) : edges[k];
                P3 pIn = k == 0 ? a.Pos : world.Nodes[nodes[k - 1]].Pos;
                P3 pOut = k == nodes.Count - 1 ? b.Pos : world.Nodes[nodes[k + 1]].Pos;
                trimAt[k] = JunctionTrim(world, nodes[k], inEdge, outEdge, pIn, pOut, side);
            }

            // 首段：a 的锚点 → startNode 沿所属边
            AppendAnchorSegment(r, a, world, firstNode, tuning, endA, endB, sideA, trimAt[0]);
            for (int i = 0; i < edges.Count; i++)
            {
                int edgeIdx = edges[i];
                GraphEdge ge = world.Edges[edgeIdx];
                int from = nodes[i];
                int to = nodes[i + 1];
                byte sideI = edgeIdx == a.Edge ? sideA : (edgeIdx == b.Edge ? sideB
                    : SideForEdge(ge, PlacedNode.SIDE_CENTRE, side, ctx));
                AppendEdgeSpan(r, ge, edgeIdx, world, from, to, tuning, endA, endB, sideI, trimAt[i], trimAt[i + 1]);
            }
            // 末段：endNode → b 的锚点
            AppendAnchorSegmentEnd(r, b, world, lastNode, tuning, endA, endB, sideB, trimAt[nodes.Count - 1]);

            // 需求 2：网络类型变了的那个接头**必须**留下一个节点。
            // 登记要按「实际走过的边序列」逐对比较，而不是只比回溯出来的 edges ——
            // 锚点挂在某条边中间时这条边的**两个**接头都会被播种，搜索常常直接从对侧接头出图，
            // 于是 edges 是空的（整条走线 = 两头各自沿所属边走一段），只比 edges 的话
            // 「道路→铁路」那个交叉点恰好落在没被比较的那一环（离线壳 X5 抓到：钉数为 0）。
            List<int> walked = new List<int>();
            List<int> joints = new List<int>();      // joints[k] = walked[k] 与 walked[k+1] 相接的那个图节点
            if (aSpan) AppendWalked(walked, joints, a.Edge, -1);
            for (int i = 0; i < edges.Count; i++) AppendWalked(walked, joints, edges[i], i == 0 ? firstNode : nodes[i]);
            if (bSpan) AppendWalked(walked, joints, b.Edge, lastNode);
            for (int k = 0; k + 1 < walked.Count; k++)
            {
                GraphEdge p = walked[k] >= 0 && walked[k] < world.Edges.Count ? world.Edges[walked[k]] : null;
                GraphEdge q = walked[k + 1] >= 0 && walked[k + 1] < world.Edges.Count ? world.Edges[walked[k + 1]] : null;
                // 反馈 4 把「切换处必须放节点」列了两种：**网络类型**变了（道路↔铁路），
                // 以及**高度档**变了（地面↔高架、两条不同标高的架开路）。
                // 前者原模组就有，后者是这一轮新读到的那句「可以切换到相同或不同高度的道路或铁路轨道」。
                if (KindChanged(p, q) || HeightChanged(p, q)) AddNodePin(r, world, joints[k]);
            }

            r.UsedNetwork = true;
            // 真实几何弧长（含末段接入），不含排序用的换类/转角惩罚。
            r.Cost = cost + endExit;
            unchecked
            {
                long sig = 1469598103934665603L;
                for (int i = 0; i < edges.Count; i++) sig = (sig ^ world.Edges[edges[i]].Signature) * 1099511628211L;
                sig = (sig ^ world.Edges[a.Edge].Signature) * 1099511628211L;
                sig = (sig ^ world.Edges[b.Edge].Signature) * 1099511628211L;
                r.Signature = sig;
            }
            r.Fallback = TraceFallback.None;
            return r;
        }

        /// <summary>A* 启发项：到终点的平面距离。不高估 ⇒ 搜索仍然最优。</summary>
        private static double Heuristic(WorldSnapshot world, int v, int endNode)
        {
            return NodePos(world, v).DistanceTo(NodePos(world, endNode));
        }

        /// <summary>进方向的单位向量与出方向的单位向量的点积（1=直行，-1=原路返回）。</summary>
        private static double TurnCosine(WorldSnapshot world, int prevNode, int node, int nextNode)
        {
            if (prevNode < 0 || node < 0 || nextNode < 0) return 1.0;
            if (prevNode >= world.Nodes.Count || node >= world.Nodes.Count || nextNode >= world.Nodes.Count) return 1.0;
            V2 inDir = (V2.From(NodePos(world, node)) - V2.From(NodePos(world, prevNode))).Normalized();
            V2 outDir = (V2.From(NodePos(world, nextNode)) - V2.From(NodePos(world, node))).Normalized();
            return inDir.Dot(outDir);
        }

        /// <summary>走过一条边的代价 = 该边在两个接头之间的实际弧长（不是端点直线距离，否则弯路会被低估）。</summary>
        private static double EdgeArcLength(GraphEdge ge, WorldSnapshot.AdjEntry en)
        {
            double len = Math.Abs(en.ArcAtOtherNode - en.ArcAtThisNode);
            if (len <= 0) len = ge.Length > 0 ? ge.Length : (ge.Line != null ? ge.Line.Length2D() : 0);
            return len;
        }

        /// <summary>
        /// 锚点可用的进/出接头。交叉口锚点就是它自己；边上的锚点是该边的两个接头（两端都给，
        /// 由搜索挑真正省的那一侧）；自由点没有锚，返回空数组 ⇒ 调用方回退直线。
        /// </summary>
        private static int[] AnchorEnds(PlacedNode p, WorldSnapshot world)
        {
            if (p.Kind == SnapKind.NetNode && p.GraphNode >= 0 && p.GraphNode < world.Nodes.Count)
            {
                return new int[] { p.GraphNode };
            }
            if (p.Edge < 0 || p.Edge >= world.Edges.Count) return EMPTY_ENDS;
            GraphEdge ge = world.Edges[p.Edge];
            if (ge == null) return EMPTY_ENDS;
            if (ge.StartNode == ge.EndNode) return new int[] { ge.StartNode };   // 自环
            return new int[] { ge.StartNode, ge.EndNode };
        }

        private static readonly int[] EMPTY_ENDS = new int[0];

        private static double[] AnchorEndCosts(PlacedNode p, WorldSnapshot world, int[] ends, byte side)
        {
            double[] c = new double[ends.Length];
            for (int i = 0; i < ends.Length; i++) c[i] = AnchorToNodeCost(p, world, ends[i], side);
            return c;
        }

        /// <summary>
        /// 锚点（贴在某条边上的点）走到它所属边的某个接头需要的弧长。
        /// 量的是 <paramref name="side"/> 那条折线自己的弧长 —— 锚点的 <c>Arc</c> 也是在游戏侧
        /// 按同一条折线探针出来的，两边口径必须一致，否则接入代价会算出负数或超出边长。
        /// </summary>
        private static double AnchorToNodeCost(PlacedNode p, WorldSnapshot world, int nodeIdx, byte side)
        {
            if (p.Kind == SnapKind.NetNode && p.GraphNode == nodeIdx) return 0;
            if (p.Edge < 0 || p.Edge >= world.Edges.Count) return p.Pos.DistanceTo(NodePos(world, nodeIdx));
            GraphEdge ge = world.Edges[p.Edge];
            double total = Total(ge, side);
            double arcAtNode = ge.StartNode == nodeIdx ? 0 : total;
            return Math.Abs(arcAtNode - p.Arc);
        }

        /// <summary>
        /// 起点侧：从 a 沿它所属边走到起始接头。a 本身就是交叉口时没有「沿边走」这一段。
        /// <paramref name="endA"/>/<paramref name="endB"/> 是**本条描边**的两个端点，只有贴着它们的点该丢
        /// （投影层会自己把这两个位置补回去），各小段自己的接头不是这个口径。
        /// </summary>
        private static void AppendAnchorSegment(TraceResult r, PlacedNode a, WorldSnapshot world, int startNode, ResolvedTuning tuning, P3 endA, P3 endB, byte side, double trimEnd)
        {
            if (a.Edge < 0 || a.Edge >= world.Edges.Count) return;
            // 锚点是网络接头（交叉口）：路径就从这个点出发，任何「沿 a.Edge 走到接头」都是多余的一段，
            // 而且 NearestEdgeOf 挑的那条边很可能根本不是要走的那条 ⇒ 会拖出一段倒退的折返线
            // （回归壳 T6：本该贴着北边的线里冒出东边路上的点）。
            if (a.Kind == SnapKind.NetNode && a.GraphNode == startNode)
            {
                if (!r.UsedEdges.Contains(a.Edge)) r.UsedEdges.Add(a.Edge);
                return;
            }
            GraphEdge ge = world.Edges[a.Edge];
            Polyline line = ge.LineFor(side);
            if (line == null || line.Count < 2) return;
            double total = Total(ge, side);
            double arcNode = ge.StartNode == startNode ? 0 : total;
            double arcA = ArcOnLine(a, line, side, total);
            AppendSpan(r, line, arcA, TrimToward(arcA, arcNode, trimEnd, total), endA, endB, tuning);
            if (!r.UsedEdges.Contains(a.Edge)) r.UsedEdges.Add(a.Edge);
        }

        /// <summary>终点侧：从末接头沿 b 所属边走到 b。b 本身就是接头时同样没有这一段。</summary>
        private static void AppendAnchorSegmentEnd(TraceResult r, PlacedNode b, WorldSnapshot world, int endNode, ResolvedTuning tuning, P3 endA, P3 endB, byte side, double trimStart)
        {
            if (b.Edge < 0 || b.Edge >= world.Edges.Count) return;
            if (b.Kind == SnapKind.NetNode && b.GraphNode == endNode)
            {
                if (!r.UsedEdges.Contains(b.Edge)) r.UsedEdges.Add(b.Edge);
                return;
            }
            GraphEdge ge = world.Edges[b.Edge];
            Polyline line = ge.LineFor(side);
            if (line == null || line.Count < 2) return;
            double total = Total(ge, side);
            double arcNode = ge.StartNode == endNode ? 0 : total;
            double arcB = ArcOnLine(b, line, side, total);
            AppendSpan(r, line, TrimToward(arcB, arcNode, trimStart, total), arcB, endA, endB, tuning);
            if (!r.UsedEdges.Contains(b.Edge)) r.UsedEdges.Add(b.Edge);
        }

        /// <summary>
        /// 锚点这条接入段按**实际要走的那条线**重新量弧长。
        /// 锚点存储的 Arc 是在它自己吸附时那条线上量的；<see cref="SideForEdge"/> 逐边定侧之后
        /// 两者可能不再是同一条折线（例如绕街区的某一格被质心判到对侧）。
        /// 弧长口径不一致的后果是「接入段从边的另一头开始」，画出来就是一根横穿街区的长线。
        /// 锚点本来就在这条线上（侧向一致）时原样用它的 Arc，不引入探针误差。
        /// </summary>
        private static double ArcOnLine(PlacedNode p, Polyline line, byte side, double total)
        {
            if (p.Side == side) return p.Arc;
            if (line == null || line.Count < 2) return p.Arc;
            P3 hit;
            int si;
            double st, al;
            double d = GeoKit.ClosestPointOnPolylineProbe(line, p.Pos, out hit, out si, out st, out al);
            // 投影都投不上（点离这条线比这条边还远）说明这条线根本不该被选中，退回原弧长并夹进范围内。
            double arc = d <= total ? al : p.Arc;
            if (arc < 0) arc = 0;
            if (arc > total) arc = total;
            return arc;
        }

        /// <summary>
        /// 这条边在本次走线里用哪条线：中心线 / 左缘 / 右缘。
        ///
        /// ⚠ **不能整条走线共用一个 Side**。「左/右」是相对每条边自己的行进方向的，
        /// 绕街区一圈时同一条内侧缘会在北边那条路上叫 LEFT、到西边那条路上变成 RIGHT。
        /// 共用一个的后果（离线壳 X9 第二次重描抓到）：边界从某个角起整体翻到马路对面，
        /// 于是「跟随」每次重算都把区域往马路那边挪几格 —— 半死不活且越跟越坏。
        ///
        /// 判据与贴合同一口径：**离区域质心近的那一条**（贴合是 OfferSide 里 ×0.8/×1.25，
        /// 这里是直接比距离）。同一判据才谈得上需求 4「一模一样的路段，两边区域自动放置的节点要完全吻合」。
        /// 锚点自己带着侧向时以锚点为准（那一格就是玩家点上去的那条线，不许改主意）。
        /// </summary>
        private static byte SideForEdge(GraphEdge ge, byte anchorSide, byte fallback, TraceContext ctx)
        {
            // fallback == CENTRE 说明这一档本来就走中心线（市辖区/瓦片）：不许被质心判侧带到缘线上去
            if (fallback == PlacedNode.SIDE_CENTRE || ge == null) return fallback;
            if (anchorSide != PlacedNode.SIDE_CENTRE) return anchorSide;
            if (!ctx.HasCentroid) return fallback;
            bool hasL = ge.LeftLine != null && ge.LeftLine.Count >= 2;
            bool hasR = ge.RightLine != null && ge.RightLine.Count >= 2;
            if (!hasL && !hasR) return PlacedNode.SIDE_CENTRE;
            if (!hasL) return PlacedNode.SIDE_RIGHT;
            if (!hasR) return PlacedNode.SIDE_LEFT;
            return DistanceTo(ge.LeftLine, ctx.Centroid) <= DistanceTo(ge.RightLine, ctx.Centroid)
                ? PlacedNode.SIDE_LEFT : PlacedNode.SIDE_RIGHT;
        }

        private static GraphEdge EdgeAt(WorldSnapshot world, int edgeIdx)
        {
            if (edgeIdx < 0 || edgeIdx >= world.Edges.Count) return null;
            return world.Edges[edgeIdx];
        }

        private static double DistanceTo(Polyline line, P3 p)
        {
            if (line == null || line.Count < 2) return double.PositiveInfinity;
            P3 hit;
            int si;
            double st, al;
            return GeoKit.ClosestPointOnPolylineProbe(line, p, out hit, out si, out st, out al);
        }

        /// <summary>
        /// cos(转弯角) 大于它就算「直穿路口」——不剪角。0.8 ≈ 37°：路口的真实转角要么接近 90°，
        /// 要么是同一条路微微折一下（游戏自己把长路拆成好几段），后者剪了反而在直路中间啃出一个缺口。
        /// </summary>
        private const double TURN_TRIM_COS = 0.8;

        /// <summary>
        /// 在这个接头上，走线的两侧各该剪掉多少米。
        ///
        /// 【为什么非剪不可】游戏把每条边的左右缘线一路采到**路口中心**（FACT：EdgeGeometry 的起止
        /// 跟着 Curve 的起止，而 Curve 是 node→node）。于是十字路口的两条内侧缘线在路口里十字相交：
        /// 沿下边那条路走到角上、再转上右边的路，拼出来的两点顺序是「先越过竖路、再退回来」，
        /// 自成一个小 X ⇒ <c>Rejects</c> 判 WouldSelfIntersect ⇒ 整条边退回直线。绕街区一圈，
        /// 每个转角都是这个下场，玩家看到的就是「贴路只在直段生效，一到路口就散」。
        ///
        /// 【剪多少】各取两幅路面中较宽的一半：两条缘线各退让 halfWidth 后正好在路口的角点上接上，
        /// 那一小段斜切就是游戏自己在路口画的缘石转角。拿不到路宽时按
        /// <see cref="PolicyKit.MIN_ASSUMED_ROAD_WIDTH"/>（3 米）算，宁少剪不误剪。
        /// </summary>
        private static double JunctionTrim(WorldSnapshot world, int nodeIdx, int inEdge, int outEdge, P3 pIn, P3 pOut, byte side)
        {
            // 中心线档不剪：相邻两条边的中心线本来就在节点上精确相接，转角是个干净的直角，
            // 剪了反而在市辖区边界上凭空啃出斜角（需求 2 的市辖区贴合要的就是直角）。
            if (side == PlacedNode.SIDE_CENTRE) return 0;
            if (inEdge < 0 || outEdge < 0) return 0;                     // 走线在这里只有一段（端点就贴着接头）：没有转角可言
            if (inEdge == outEdge) return 0;                             // 同一条边折进又折出（搜出来的怪路径）：不是路口
            if (nodeIdx < 0 || nodeIdx >= world.Nodes.Count) return 0;
            GraphEdge a = inEdge < world.Edges.Count ? world.Edges[inEdge] : null;
            GraphEdge b = outEdge < world.Edges.Count ? world.Edges[outEdge] : null;
            if (a == null || b == null) return 0;
            P3 q = NodePos(world, nodeIdx);
            V2 inDir = (V2.From(q) - V2.From(pIn)).Normalized();
            V2 outDir = (V2.From(pOut) - V2.From(q)).Normalized();
            if (inDir.Length <= 1e-9 || outDir.Length <= 1e-9) return 0; // 重合点：方向都定不出来，不动
            if (inDir.Dot(outDir) > TURN_TRIM_COS) return 0;             // 直穿路口
            return Math.Max(HalfWidth(a, side), HalfWidth(b, side));
        }

        /// <summary>
        /// 这一侧的半幅宽（中心线到人行道外缘）。反馈 3 之后左右不再相等：
        /// 拿不到该侧数据（老快照/夹具没填）时退回 <c>RoadWidth/2</c>，再拿不到按最小路宽 3 米的一半，
        /// 宁少剪不误剪 —— 剪多了就是在直路中间啃出一个缺口。
        /// </summary>
        private static double HalfWidth(GraphEdge ge, byte side)
        {
            double w = side == PlacedNode.SIDE_LEFT ? ge.LeftHalfWidth
                       : side == PlacedNode.SIDE_RIGHT ? ge.RightHalfWidth : 0;
            if (w > 0) return w;
            if (ge.RoadWidth > 0) return ge.RoadWidth * 0.5;
            return PolicyKit.MIN_ASSUMED_ROAD_WIDTH * 0.5;
        }

        /// <summary>
        /// 起点/终点接入段是否**真的**沿锚点所属边走了一段。条件与 <see cref="AppendAnchorSegment"/>
        /// 那三道 return 一一对应：这里和那里判得不一样，钉就会登记在不存在的段上
        /// （或者该登记的段没钉 —— 两种都是「实机上偶发丢角点」这种最难查的形状）。
        /// </summary>
        private static bool AnchorSpanWalked(PlacedNode p, WorldSnapshot world, int nodeIdx, byte side)
        {
            if (p.Edge < 0 || p.Edge >= world.Edges.Count) return false;
            if (p.Kind == SnapKind.NetNode && p.GraphNode == nodeIdx) return false;   // 人就在接头上，没有「沿边走」这一段
            GraphEdge ge = world.Edges[p.Edge];
            Polyline line = ge == null ? null : ge.LineFor(side);
            if (line == null || line.Count < 2) return false;
            double arcNode = ge.StartNode == nodeIdx ? 0 : Total(ge, side);
            return System.Math.Abs(arcNode - p.Arc) > 1e-9;
        }

        /// <summary>往「走过的边序列」里追加一条边；同一条边连续出现时不记接头（接头在边内部，不是换类点）。</summary>
        private static void AppendWalked(List<int> walked, List<int> joints, int edge, int jointNode)
        {
            if (walked.Count > 0 && walked[walked.Count - 1] == edge) return;
            if (walked.Count > 0) joints.Add(jointNode);
            walked.Add(edge);
        }

        private static void AppendEdgeSpan(TraceResult r, GraphEdge ge, int edgeIndex, WorldSnapshot world, int from, int to, ResolvedTuning tuning, P3 endA, P3 endB, byte side, double trimFrom, double trimTo)
        {
            if (ge == null) return;
            Polyline line = ge.LineFor(side);
            if (line == null || line.Count < 2) return;
            double total = Total(ge, side);
            double arcFrom = ge.StartNode == from ? 0 : total;
            double arcTo = ge.StartNode == to ? 0 : total;
            // 边的 StartNode/EndNode 与搜索方向不一致时（图是无向的），用节点实际坐标兜底定弧长方向。
            if (ge.StartNode != from && ge.EndNode != from) arcFrom = ge.StartNode == to ? total : 0;
            // 两端的剪角合计不许越过整段（越过去就是首尾倒置，AppendSpan 会画出一段倒退的线）。
            double spanLen = Math.Abs(arcTo - arcFrom);
            double travel = arcTo >= arcFrom ? 1 : -1;
            double tIn = Math.Min(Math.Max(trimFrom, 0), spanLen);
            double tOut = Math.Min(Math.Max(trimTo, 0), spanLen - tIn);
            AppendSpan(r, line, arcFrom + travel * tIn, arcTo - travel * tOut, endA, endB, tuning);
            if (edgeIndex >= 0 && !r.UsedEdges.Contains(edgeIndex)) r.UsedEdges.Add(edgeIndex);
        }

        /// <summary>
        /// 把 <paramref name="target"/> 沿「<paramref name="from"/> → <paramref name="target"/>」的方向往回缩
        /// <paramref name="trim"/> 米。<b>允许缩到整段消失</b>：环上正好有一格落在路口斜切点上时
        /// （跟随重描的常态），它那 6 米的接入段本来就该整段不吃，缩成零长 ⇒ 一个点都不产出，
        /// 角上仍然是连续的一条线。留一小截尾巴（老写法按 25% 夹死）才是幂等性的破坏者：
        /// 第一遍的斜切点 (94,6) 到第二遍会变成 (98.5,6)，两遍的形状差 4.5 米（离线壳 X9 抓到）。
        /// </summary>
        private static double TrimToward(double from, double target, double trim, double total)
        {
            if (trim <= 0 || total <= 0) return target;
            double t = Math.Min(trim, Math.Abs(target - from));
            if (t <= 0) return target;
            double dir = target >= from ? 1 : -1;
            double v = target - dir * t;
            if (v < 0) v = 0;
            if (v > total) v = total;
            return v;
        }

        /// <summary>
        /// 走了侧线时，侧线缺数据就退回中心线，长度也按同一条折线量 ——
        /// 两侧长度必然不同（外圈比内圈长），混用会把弧长位置算到段外去。
        /// </summary>
        private static double Total(GraphEdge ge, byte side)
        {
            Polyline line = ge.LineFor(side);
            if (line == null || line.Count < 2) return 0;
            if (side == PlacedNode.SIDE_CENTRE) return ge.Length > 0 ? ge.Length : line.Length2D();
            return line.Length2D();
        }

        /// <summary>
        /// 两条边的「网络类型」是否变了。
        /// 先看 <see cref="GraphEdge.Kind"/>（Road/Rail/Path/Waterway 的粗分类），
        /// 再比 <see cref="GraphEdge.LayerBits"/>：玩家说的「从道路变成了铁路」Kind 就能抓住，
        /// 但「普通道路 ↔ 公共交通道路」（Layer.Road ↔ Layer.PublicTransportRoad）在 Kind 里同为 Road，
        /// 只有游戏自己的 Layer 位分得开 —— 需求 2 要的是「类型发生变化的那个交叉点」，所以两层都比。
        /// 两边都是 0（prefab 没读到 Layer）时退化成只比 Kind，不误报。
        /// </summary>
        private static bool KindChanged(GraphEdge p, GraphEdge q)
        {
            if (p == null || q == null) return false;
            if (p.Kind != q.Kind) return true;
            if (p.LayerBits == 0 && q.LayerBits == 0) return false;
            return p.LayerBits != q.LayerBits;
        }

        private static void AddNodePin(TraceResult r, WorldSnapshot world, int nodeIdx)
        {
            if (nodeIdx < 0 || nodeIdx >= world.Nodes.Count) return;
            r.Pins.Add(NodePos(world, nodeIdx));
        }

        /// <summary>钉住某个接头（反馈 4「切换处必须放节点」里的「切换处」就是这个点）。</summary>
        private static void AddNodePinAt(TraceResult r, P3 p)
        {
            r.Pins.Add(p);
        }

        /// <summary>
        /// 两条边在**高度档**上是不是不同（反馈 4：市辖区允许切换到「相同或不同高度」的道路/轨道，
        /// 而切换处必须放节点）。
        ///
        /// 判据用游戏的两个位（FACT：NetCompositionData/NetInfo 上 m_Flags 有 Layer.ElevatedRoad1/2、
        /// ElevatedRail、ElevatedPedestrianNode 那一族，标高等于 Layer 名里的档号）：
        ///  · 地面 ↔ 高架 ⇒ Elevated 不同，一定是一个切换处；
        ///  · 两条都是高架 ⇒ 再比 Lift（ElevatedRoad1 与 ElevatedRoad2 是两条不同标高的带）；
        ///  · 两条都在地面 ⇒ 没有高度可切（隧道另有 Tunnel 一档，但隧道根本不进边线档，见 IsTraceable）。
        /// 阈值 0.5 米：同一档的采样误差不该被当成「换了高度」。
        /// </summary>
        private static bool HeightChanged(GraphEdge p, GraphEdge q)
        {
            if (p == null || q == null) return false;
            if (p.Elevated != q.Elevated) return true;
            if (!p.Elevated) return false;
            return Math.Abs(p.Lift - q.Lift) > 0.5;
        }

        /// <summary>
        /// 这条边（两个接头之间的段）在不在有向弦 A→B 指定的一侧。
        ///
        /// 用**边中点**到弦的有符号垂距判定，而不是用端点：一条横穿弦的边（就是「穿街区的那条小巷」
        /// 本身）两个端点分居两侧，按端点判它两边都算、按中点判它压在弦上 —— 压在弦上的边两种侧向都放行，
        /// 因为「绕左侧」与「绕右侧」的路线都必然从弦的两端接出去，那两段就贴着弦。
        /// </summary>
        private static bool OnSideOfChord(WorldSnapshot world, int u, int v, P3 from, V2 chordDir, int sideFilter)
        {
            if (u < 0 || v < 0 || u >= world.Nodes.Count || v >= world.Nodes.Count) return true;
            if (chordDir.Length <= 1e-12) return true;      // 两端点重合：没有侧向可言，全放行
            P3 mid = P3.Lerp(NodePos(world, u), NodePos(world, v), 0.5);
            V2 off = V2.From(mid) - V2.From(from);
            double lat = chordDir.CrossZ(off);
            if (System.Math.Abs(lat) <= CHORD_SLACK) return true;
            return lat > 0 ? sideFilter > 0 : sideFilter < 0;
        }

        /// <summary>
        /// 折线子段 → 结果点集，自动判方向。
        ///
        /// ⚠ 过滤只针对「离本条描边端点（endA/endB）太近」的点。老代码在这里传的是**各小段自己的两端**，
        /// 于是每一段把接头处那个顶点当成「贴着端点」给丢了 —— 相邻两段又各丢一次，
        /// 结果路网每个拐角处都缺一颗点：折尺路 (7,0)→(20,0)→(20,20)→(23,20) 交出去的是
        /// (18,0)(20,2)(20,18) 这种被削掉角的斜线（回归壳 T3 抓到；SimplifyKit 单层测是好的，
        /// 因为点根本没走到简化那一步就已经缺了）。
        /// </summary>
        private static void AppendSpan(TraceResult r, Polyline line, double arcA, double arcB, P3 endA, P3 endB, ResolvedTuning tuning)
        {
            bool reversed = arcB < arcA;
            Polyline span = GeoKit.Subspan(line, reversed ? arcB : arcA, reversed ? arcA : arcB);
            if (span.Count == 0) return;
            List<P3> pts = span.Points;
            if (reversed)
            {
                for (int i = pts.Count - 1; i >= 0; i--) PushMid(r, pts[i], endA, endB, tuning);
            }
            else
            {
                for (int i = 0; i < pts.Count; i++) PushMid(r, pts[i], endA, endB, tuning);
            }
            r.Cost += span.Length2D();
        }

        private static void PushMid(TraceResult r, P3 p, P3 posA, P3 posB, ResolvedTuning tuning)
        {
            if (p.DistanceTo(posA) < tuning.MinSpacing) return;
            if (p.DistanceTo(posB) < tuning.MinSpacing) return;
            // 只挡「同一个顶点重复登记」：相邻两段在接头处会把同一个顶点各推一遍。
            // 这里**不做**按 MinSpacing 抽稀 —— 抽稀是 SimplifyKit 的活；在密采样点列上贪心跳点
            // 会连拐角一起跳掉（回归壳 T3：折尺路的 (20,0) 与 (20,20) 两个拐角全被吃，
            // 玩家看到的就是「明明贴着路走了，交出去的两点却把拐角抹成一条斜线」）。
            if (r.Points.Count > 0 && r.Points[r.Points.Count - 1].DistanceTo(p) <= 1e-9) return;
            r.Points.Add(p);
        }
    }

    /// <summary>
    /// 二叉堆（(double 代价, int 节点) 对）。
    /// 为什么不用 SortedSet / PriorityQueue：net48 + LangVersion 9 没有 System.PriorityQueue，
    /// 而 SortedSet 的自定义比较器在「同代价不同节点」时会互相顶掉，Dijkstra 需要允许重复入堆。
    /// </summary>
    internal sealed class MinHeap
    {
        private int[] _nodes;
        private double[] _prios;
        private int _count;

        public MinHeap(int capacity)
        {
            if (capacity < 8) capacity = 8;
            _nodes = new int[capacity];
            _prios = new double[capacity];
            _count = 0;
        }

        public int Count { get { return _count; } }

        public void Push(int node, double prio)
        {
            if (_count == _nodes.Length) Grow();
            int i = _count++;
            _nodes[i] = node;
            _prios[i] = prio;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_prios[parent] <= _prios[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        public bool Pop(out int node, out double prio)
        {
            if (_count == 0) { node = -1; prio = 0; return false; }
            node = _nodes[0];
            prio = _prios[0];
            _count--;
            if (_count > 0)
            {
                _nodes[0] = _nodes[_count];
                _prios[0] = _prios[_count];
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1;
                    int r = i * 2 + 2;
                    int smallest = i;
                    if (l < _count && _prios[l] < _prios[smallest]) smallest = l;
                    if (r < _count && _prios[r] < _prios[smallest]) smallest = r;
                    if (smallest == i) break;
                    Swap(smallest, i);
                    i = smallest;
                }
            }
            return true;
        }

        private void Swap(int a, int b)
        {
            int tn = _nodes[a]; _nodes[a] = _nodes[b]; _nodes[b] = tn;
            double tp = _prios[a]; _prios[a] = _prios[b]; _prios[b] = tp;
        }

        private void Grow()
        {
            int cap = _nodes.Length * 2;
            Array.Resize(ref _nodes, cap);
            Array.Resize(ref _prios, cap);
        }
    }

    /// <summary>
    /// 把「已经画出来的那部分环」塞给描边（绕行方向、自交与退化判定都要它）。
    ///
    /// 这里原来是游戏侧 <c>Systems/ZoneSnapperSystem.cs</c> 里的 internal 工具，v0.1.4 搬到 Engine：
    /// 预览层（<see cref="PreviewKit"/>）要用**同一份**口径造上下文，留在游戏侧它就够不着 ——
    /// 复制一份必然漂移，而漂移的症状是「已落档的边贴合，预览那条边贴得乱七八糟」。
    /// </summary>
    public static class TraceContextUtil
    {
        public static void Fill(IList<PlacedNode> nodes, ref TraceContext ctx)
        {
            Build(ManualRing(nodes, null), ref ctx);
        }

        /// <summary>
        /// 预览专用：把「还没落下的游标格」也算进环里。
        /// 为什么要算：开放路径的最后一段（最后一格 → 游标）本来不在任何环里，
        /// 描边按 <see cref="TraceContext.RingEdge"/> 找不到那一格，就会退回「环 + 整条走线首尾相接」的
        /// 老拼法 —— 对开放路径等于凭空多画一条弦，预览的每条边都被判成 WouldSelfIntersect。
        /// 补上游标之后，那条弦就是我们真正要替换的段，格号也清楚了。
        /// </summary>
        public static void FillWithCursor(IList<PlacedNode> nodes, P3 cursor, ref TraceContext ctx)
        {
            Build(ManualRing(nodes, cursor), ref ctx);
        }

        private static List<P3> ManualRing(IList<PlacedNode> nodes, P3? extra)
        {
            List<P3> ring = new List<P3>();
            if (nodes != null) for (int i = 0; i < nodes.Count; i++) ring.Add(nodes[i].Pos);
            if (extra.HasValue) ring.Add(extra.Value);
            return ring;
        }

        private static void Build(List<P3> ring, ref TraceContext ctx)
        {
            ctx.ExistingRing = null;
            ctx.HasCentroid = false;
            ctx.Orientation = 0;
            if (ring.Count < 3) return;      // 凑不成环 ⇒ 绕行方向与自交判定都无从谈起
            ctx.ExistingRing = ring;
            double sx = 0, sy = 0;
            for (int i = 0; i < ring.Count; i++) { sx += ring[i].X; sy += ring[i].Y; }
            ctx.HasCentroid = true;
            ctx.Centroid = new P3(sx / ring.Count, sy / ring.Count, 0);
            ctx.Orientation = Math.Sign(GeoKit.SignedArea2D(ring));
        }
    }
}
