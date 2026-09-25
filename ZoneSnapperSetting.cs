using System;
using System.Collections.Generic;
using Colossal;
using Game;
using Game.Input;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Localization;
using Game.UI.Widgets;
using ZoneSnapper.Engine;
using ZoneSnapper.GameSide;

namespace ZoneSnapper
{
    /// <summary>
    /// Zone Snapper 选项页（官方 ModSetting 声明式框架，需求 6：功能全部做成开关 + 独立快捷键标签页）。
    ///
    /// 【第五轮反馈后的结构】玩家说得很具体，这里逐条落：
    ///  · 标签页只有三个：**吸附** / **其它设置** / **快捷键**（反馈 7）。原来那一整页「描边与节点」删掉了 ——
    ///    里面除了曲线精细度与跟随，其余全是「一条规则要不要生效」的开关，而玩家已经把这些规则定成默认行为
    ///    （描边永远允许、节点上限无限、环岛沿自己的中心走），留着只会让人以为关了会不一样。
    ///  · 生效的区域类型只剩游戏「区域」工具里那三个工具（反馈 2），名字用游戏自己的用词（反馈 9）：
    ///    市辖区 / 专门产业区 / 表面区域；城市边界线挪到最下面的「特殊吸附目标」；太空区域整条删除（反馈 6）。
    ///  · 贴合范围改成**按类别各一条**「节点吸附距离」（反馈 3）：50%~300%，默认 100% = 游戏原版距离。
    ///    原来那条统一倍率 + 行政区额外倍率删掉：两层相乘之后没人能反推自己调的是什么。
    ///  · 「贴合优先级」那一板块整个换成**贴合模式**下拉框（反馈 5）。
    ///
    /// 【框架约束（不变）】照 Playbook §3.2 与 1.6.2 反编译实证：
    ///  · 框架在主线程直接调 setter ⇒ 每个 setter 尾部 Sync() 即时生效；热路径仍读 SnapperState.Config
    ///    这份 volatile 静态镜像，不读属性链（硬规则 15）。
    ///  · 滑杆只用整数（FACT：Game.Settings/AudioSettings.cs:24 是 unit="percentage"，默认 "integer"）。
    ///  · 任何 public string 选项行都不得返回 null（否则玩家一点开该标签页就 ArgumentNullException）。
    ///    ⇒ 下拉框用**枚举属性**：FACT：Game.UI.Menu/AutomaticSettings.cs:1131-1138 枚举类型走
    ///    WidgetType.EnumDropdown，且 :846-855 会给每个成员生成一条 "Options.{枚举名大写}[成员名]" 的词条
    ///    —— 我们自己注册这几个 key（见 BuildLocaleMap 末尾那段），玩家看到的才是中文选项名。
    ///  · 快捷键行的标题/说明用 GetOptionLabelLocaleID(nameof(ProxyBinding 属性))，
    ///    不是 GetBindingKeyLocaleID（那是输入系统动作名）——Playbook 记着两个模组各踩过一次。
    ///  · 未绑键的 action 不置 shouldBeEnabled=true，否则会吃下整块键盘设备（§3.2 的「幻影触发」）。
    /// </summary>
    [SettingsUITabOrder(kTabSnap, kTabOther, kTabKeys)]
    [SettingsUIGroupOrder(kGroupMain, kGroupTools, kGroupDistance, kGroupMode, kGroupSpecial,
                          kGroupCurve, kGroupContinuation, kGroupFollow, kGroupPreview,
                          kGroupKeysSnap, kGroupKeysArea, kGroupKeysReset, kGroupAbout)]
    [SettingsUIShowGroupName(kGroupMain, kGroupTools, kGroupDistance, kGroupMode, kGroupSpecial,
                             kGroupCurve, kGroupContinuation, kGroupFollow, kGroupPreview,
                             kGroupKeysSnap, kGroupKeysArea, kGroupKeysReset, kGroupAbout)]
    [SettingsUIKeyboardAction(kActionToggleEnabled)]
    [SettingsUIKeyboardAction(kActionCycleMode)]
    [SettingsUIKeyboardAction(kActionRetraceNow)]
    [SettingsUIKeyboardAction(kActionUndoFollow)]
    public class ZoneSnapperSetting : ModSetting
    {
        public const string kTabSnap = "Snap";
        public const string kTabOther = "Other";
        public const string kTabKeys = "Keys";

        public const string kGroupMain = "MainSwitch";
        public const string kGroupTools = "Tools";
        public const string kGroupDistance = "SnapDistance";
        public const string kGroupMode = "Mode";
        public const string kGroupSpecial = "SpecialTargets";
        public const string kGroupCurve = "CurveDetail";
        public const string kGroupContinuation = "Continuation";
        public const string kGroupFollow = "Follow";
        public const string kGroupPreview = "Preview";
        public const string kGroupKeysSnap = "KeysSnap";
        public const string kGroupKeysArea = "KeysArea";
        public const string kGroupKeysReset = "KeysReset";
        public const string kGroupAbout = "About";

        /// <summary>
        /// 「关于」那三颗跳转按钮共用的按钮组名（同一行显示）。
        /// ⚠ 这张表是游戏进程内的 static 全局表（FACT：AutomaticSettings.cs:709、:715-731），
        /// 组名撞了就等于跟别人的按钮并成一行 ⇒ 带模组前缀。
        /// </summary>
        public const string kAboutButtonGroup = "ZoneSnapper.ZoneSnapperSetting.AboutLinks";

        /// <summary>作者（第八轮反馈 1：玩家本人署名）。</summary>
        public const string kAuthor = "yuexian";

        public const string kUrlKoFi = "https://ko-fi.com/yuexian7";

        /// <summary>论坛页：目前挂的是同作者系列（Access Anarchy）的帖子 —— 本模组尚未发布，还没有自己的帖子。</summary>
        public const string kUrlForum = "https://forum.paradoxplaza.com/forum/threads/access-anarchy.1941285/latest";

        public const string kUrlRainbow = "https://rainbow-series-hvpma89wi25.qoder.zone/#top";

        /// <summary>
        /// 「重置所有设置项」那颗按钮的**确认弹窗**词条 key。
        /// 口径（FACT：Game.UI.Menu/AutomaticSettings.cs:1146-1158 <c>GetConfirmationMessage</c>）：
        /// 属性上带 <c>[SettingsUIConfirmation(confirmMessageId, confirmMessageValue)]</c> 且 id 非空时，
        /// 游戏查的是 <c>"Options.WARNING[" + confirmMessageId + "]"</c>，并且用
        /// <c>LocalizedString.IdWithFallback(id, value)</c> ⇒ 词条没注册上时退到第二个参数那句兜底文本
        /// （不会像下拉选项那样把 key 直接糊到屏幕上）。
        /// id 自己拼一份、注册时再拼一次，两处必须一致，所以做成常量而不是散在字面量里。
        /// </summary>
        public const string kResetConfirmKey = kSettingId + ".ZoneSnapperSetting.ResetAllSettings";

        /// <summary>确认弹窗的兜底文本（词条缺失时才用得上；平时玩家看到的是本地化过的那份）。</summary>
        public const string kResetConfirmText = "确定要把本模组的所有设置项恢复成默认值吗？快捷键也会被清空，此操作无法撤销。";

        public const string kActionToggleEnabled = "ToggleSnapper";
        public const string kActionCycleMode = "CycleSnapMode";
        public const string kActionRetraceNow = "RetraceTracedAreas";
        public const string kActionUndoFollow = "UndoLastFollow";

        /// <summary>系统读取入口（OnLoad 赋值，OnDispose 置空）。</summary>
        public static ZoneSnapperSetting Instance;

        private Systems.ZoneSnapperSystem m_system;

        public ZoneSnapperSetting(IMod mod) : base(mod) { }

        // —————————————————————————— 总开关（反馈 2：默认打开）

        private bool m_Enabled = true;

        /// <summary>总开关。关掉后系统每帧第一道闸就返回，游戏工具完全按原逻辑走。</summary>
        [SettingsUISection(kTabSnap, kGroupMain)]
        public bool Enabled
        {
            get { return m_Enabled; }
            set { if (m_Enabled == value) return; m_Enabled = value; SnapperState.Enabled = value; Sync(); }
        }

        // —————————————————————————— 生效的区域类型：三个工具（反馈 2）
        //
        // 名字必须是游戏「区域」工具里那三个工具的官方叫法（反馈 9）：市辖区 / 专门产业区 / 表面区域。
        // 关掉一个 ⇒ 该工具整个交还原游戏：我们既不吸也不描边（但游戏自带的节点吸附仍然在，
        // 这一点在说明行里写清楚了，否则玩家会以为开关坏了）。

        [SettingsUISection(kTabSnap, kGroupTools)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool SnapDistrict { get; set; } = true;

        [SettingsUISection(kTabSnap, kGroupTools)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool SnapLot { get; set; } = true;

        [SettingsUISection(kTabSnap, kGroupTools)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool SnapSurface { get; set; } = true;

        // —————————————————————————— 节点吸附距离：每类各一条（反馈 3）
        //
        // 基准是游戏自己给这一类区域配的 AreaGeometryData.m_SnapDistance ⇒ 100% 就是原版距离。
        // 三条互不影响：市辖区贴中心线、产业区/表面区域贴人行道外缘，本来就是两回事，
        // 上一版用一条统一倍率再乘一个「行政区额外倍率」，两个数相乘之后没人能反推自己调的是什么。

        private int m_DistrictSnapPercent = 100;

        [SettingsUISection(kTabSnap, kGroupDistance)]
        [SettingsUISlider(min = 50, max = 300, step = 10, unit = "integer")]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NoDistrict))]
        public int DistrictSnapPercent
        {
            get { return m_DistrictSnapPercent; }
            set { int v = Clamp(value, 50, 300); if (m_DistrictSnapPercent == v) return; m_DistrictSnapPercent = v; Sync(); }
        }

        private int m_LotSnapPercent = 100;

        [SettingsUISection(kTabSnap, kGroupDistance)]
        [SettingsUISlider(min = 50, max = 300, step = 10, unit = "integer")]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NoLot))]
        public int LotSnapPercent
        {
            get { return m_LotSnapPercent; }
            set { int v = Clamp(value, 50, 300); if (m_LotSnapPercent == v) return; m_LotSnapPercent = v; Sync(); }
        }

        private int m_SurfaceSnapPercent = 100;

        [SettingsUISection(kTabSnap, kGroupDistance)]
        [SettingsUISlider(min = 50, max = 300, step = 10, unit = "integer")]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NoSurface))]
        public int SurfaceSnapPercent
        {
            get { return m_SurfaceSnapPercent; }
            set { int v = Clamp(value, 50, 300); if (m_SurfaceSnapPercent == v) return; m_SurfaceSnapPercent = v; Sync(); }
        }

        // —————————————————————————— 贴合模式（反馈 5：取代整个「贴合优先级」板块）
        //
        // 玩家要的是「这一次画的时候该怎么选路」，而上一版把它拆成三个各自独立的开关
        // （优先交叉口 / 优先已有边界 / 允许贴路缘），谁赢没有定义。模式把那层取舍收回来，
        // 每个模式就是一组具体数字（Engine/PolicyKit.cs 的 ModeKnobs），默认「智能」。

        [SettingsUISection(kTabSnap, kGroupMode)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public TraceMode SnapMode { get; set; } = TraceMode.Smart;

        // —————————————————————————— 特殊吸附目标（反馈 2 的「另外 2 个」+ 反馈 6 改名）
        //
        // 太空区域整条删除（玩家：屋顶没必要，道路边缘上面已经包括了）；
        // 地图瓦片改名「城市边界线」并留在这里，默认关 —— 游戏自己都不让这个工具吸附，
        // 我们替它吸一次等于在玩家没要求的东西上改形状。

        [SettingsUISection(kTabSnap, kGroupSpecial)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool SnapMapTile { get; set; } = false;

        private int m_MapTileSnapPercent = 100;

        [SettingsUISection(kTabSnap, kGroupSpecial)]
        [SettingsUISlider(min = 50, max = 300, step = 10, unit = "integer")]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NoMapTile))]
        public int MapTileSnapPercent
        {
            get { return m_MapTileSnapPercent; }
            set { int v = Clamp(value, 50, 300); if (m_MapTileSnapPercent == v) return; m_MapTileSnapPercent = v; Sync(); }
        }

        // —————————————————————————— 其它设置：曲线精细度（反馈 7）
        //
        // 就是原来的「简化程度」，改名 + 换方向 + 默认 0：
        //  · 0 = 只在交点放节点（反馈 4：没有交点就不需要放节点）；
        //  · 调高 = 弯道上补点，回到上一版那种「相邻两点连线不许越出道路边缘」的严丝合缝。
        // 交点在任何一档都必放：这一档只决定平滑弯道额外补多少点。

        private int m_CurveDetailPercent = 0;

        [SettingsUISection(kTabOther, kGroupCurve)]
        [SettingsUISlider(min = 0, max = 100, step = 5, unit = "integer")]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public int CurveDetailPercent
        {
            get { return m_CurveDetailPercent; }
            set { int v = Clamp(value, 0, 100); if (m_CurveDetailPercent == v) return; m_CurveDetailPercent = v; Sync(); }
        }

        // —————————————————————————— 节点续接（反馈 7 新增，取代「自由点之间走弧线」）

        [SettingsUISection(kTabOther, kGroupContinuation)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool NodeContinuation { get; set; } = true;

        // —————————————————————————— 重算已提交区域（反馈 8 新增，第六轮反馈 5 改语义）
        //
        // 「绘制过程实时跟随路网」这一档删了：绘制过程中玩家不可能同时去改路，那条永远不触发。
        // 第六轮又删了第二条自动路径：**路网/建筑变了不再自动改写存档**，只在日志里提示一句
        // （玩家原话「按了键才修改，不是拖拽节点就触发跟随」「目前看不太靠谱」）。
        // 所以现在这一档管的是「按『重算贴合区域』键时，允不允许改写视野内已保存的区域」——
        // 写盘永远由玩家那一击发起，默认开着（关着的话那颗键什么都不会做，玩家只会以为模组坏了）。
        // 配套的「退回上一次自动跟随」快捷键在快捷键页，撤销的就是这颗键写的那一批。
        // 注意：拖拽改形后的重描**不受这一档管**（那是玩家亲手拖的结果，见 ZoneSnapperFollowSystem）。

        [SettingsUISection(kTabOther, kGroupFollow)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool FollowCommitted { get; set; } = true;

        /// <summary>跟随改完之后把那块区域的边界描一圈、被挪动的节点闪几下再消失（反馈 8 对「显示贴合提示」的重定义）。</summary>
        [SettingsUISection(kTabOther, kGroupFollow)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NoFollow))]
        public bool HighlightFollowed { get; set; } = true;

        // —————————————————————————— 实时预览（从「贴合」页挪到这里，反馈 7）

        [SettingsUISection(kTabOther, kGroupPreview)]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NotEnabled))]
        public bool ShowLivePreview { get; set; } = true;

        private int m_PreviewFadePercent = 35;

        /// <summary>预览色的不透明度（%）。刻意低于实线，避免把预览看成已提交结果。</summary>
        [SettingsUISection(kTabOther, kGroupPreview)]
        [SettingsUISlider(min = 10, max = 90, step = 5, unit = "integer")]
        [SettingsUIHideByCondition(typeof(ZoneSnapperSetting), nameof(NoPreview))]
        public int PreviewFadePercent
        {
            get { return m_PreviewFadePercent; }
            set { int v = Clamp(value, 10, 90); if (m_PreviewFadePercent == v) return; m_PreviewFadePercent = v; Sync(); }
        }

        // —————————————————————————— 快捷键
        //
        // 口径（v0.1.1 实机反馈后定的，没变）：**一律默认留空**，由玩家自己在这一页设。
        // 带默认值会撞玩家已经用惯的键，还会进游戏就吃一条「冲突」提示。
        //
        // 反馈 8 新增第四颗：「退回上一次自动跟随的调整」—— 玩家明确要求把「强烈建议设置」写进说明里。

        [SettingsUISection(kTabKeys, kGroupKeysSnap)]
        [SettingsUIKeyboardBinding(BindingKeyboard.None, kActionToggleEnabled)]
        public ProxyBinding ToggleEnabledBinding { get; set; }

        /// <summary>在五档贴合模式之间循环（原来那颗「切换贴合优先级」换成它，反馈 5）。</summary>
        [SettingsUISection(kTabKeys, kGroupKeysSnap)]
        [SettingsUIKeyboardBinding(BindingKeyboard.None, kActionCycleMode)]
        public ProxyBinding CycleModeBinding { get; set; }

        [SettingsUISection(kTabKeys, kGroupKeysArea)]
        [SettingsUIKeyboardBinding(BindingKeyboard.None, kActionRetraceNow)]
        public ProxyBinding RetraceNowBinding { get; set; }

        [SettingsUISection(kTabKeys, kGroupKeysArea)]
        [SettingsUIKeyboardBinding(BindingKeyboard.None, kActionUndoFollow)]
        public ProxyBinding UndoFollowBinding { get; set; }

        // —————————————————————————— 重置（第六轮之后、实机测试前玩家额外要的一颗按钮）
        //
        // 【这颗按钮是怎么变成按钮的】游戏自己的算法：属性类型是 bool 且带 [SettingsUIButton] ⇒ 控件类型
        // 是 BoolButton（再带 [SettingsUIConfirmation] 就是 BoolButtonWithConfirmation）
        //（FACT：Game.UI.Menu/AutomaticSettings.cs:1051-1060 GetWidgetType —— bool 里这一支排在最前，
        //  排在 canRead/canWrite 那两条前面 ⇒ **有没有 getter 都还是按钮**）。
        // 点击时它只做一件事：`property.SetValue(setting, true)`
        //（FACT：同文件 :1169-1183 AddBoolButtonWithConfirmationProperty 里的 action），
        // 所以不需要事后把布尔翻回 false。弹窗文本口径：带 confirmMessageId 时查的是
        // "Options.WARNING[" + confirmMessageId + "]"，且用 LocalizedString.IdWithFallback
        //（FACT：同文件 :1146-1158 + SettingsUIConfirmationAttribute.cs:11-15 的参数顺序 id 在前）
        // ⇒ 词条没注册上只是退回第二个参数的兜底文本，不会像下拉选项那样把 key 糊到屏幕上。
        // 判据来源不是我发明的：游戏的「全部恢复默认」那颗按钮就是同一个写法
        //（FACT：Game.Settings/GeneralSettings.cs:138-146 的 [SettingsUIButton] + [SettingsUIConfirmation]
        //  的 set-only bool `resetSettings`；工具链那几颗"安装/卸载"按钮同理，见 ModdingSettings.cs:36-93）。
        //
        // 【getter 我们还是加了 —— 这不是照抄游戏，是抄游戏的另一半】同一份 GeneralSettings 里另一颗按钮
        // `autoSaveNow` 就带 getter（FACT：:102-118）。游戏没解释为什么有的有有的没有；我们的理由是这条：
        // 设置基类自己重写了 GetHashCode，它**无保护地**对每个 public 实例属性调 GetValue(this)
        //（FACT：Game.Settings/Setting.cs:62-71；同文件的 Equals 在 :54 反倒**有** CanRead 保护）。
        // 恒 false 的 getter 还顺带管住另一半：模组设置要走 JSON 编解码，属性被读回/写回时递的是 false，
        // 由下面那句 `if (!value) return;` 吃掉 ⇒ 不会出现「加载设置时凭空全量重置一次」。
        //
        // 【为什么这一颗能带 getter、下面「关于」那三颗不能带】游戏两条分支的门槛不一样，这是逐字读出来的：
        //  · BoolButton（不带确认弹窗）的构造函数第一行就是 `if (property.canRead || !property.canWrite) return null;`
        //    ⇒ **带 getter 的普通按钮整行不显示**（FACT：Game.UI.Menu/AutomaticSettings.cs:1221-1224）。
        //  · BoolButtonWithConfirmation 那一支**没有**这道判空，读不读值都照建（FACT：同文件 :1169-1191）。
        // ⇒ 带确认弹窗的按钮（这颗）给 getter 是纯赚（既躲开上面那条地雷又能显示）；
        //   不带确认弹窗的按钮（关于页那三颗跳转）只能 set-only —— 那也正是游戏自己的写法
        //  （FACT：Game.Settings/ModdingSettings.cs:35-79 三颗 set-only 按钮 + 同一个 ButtonGroup）。
        //   set-only 属性编码器根本不写它（读不到值），所以盘里永远不会有那个键 ⇒ 解码那一侧也碰不到它。
        //   ⇒ 门禁 T2c 钉的就是这条**分叉**规则，不是「一律要有 getter」。
        //
        // 【为什么要确认弹窗】这颗按钮会清掉玩家自己配好的快捷键（游戏允许默认留空，玩家要一个个重新设），
        // 误点一次的代价比少一个开关大得多。弹窗文本走本地化词条（见 kResetConfirmKey 的注释）。

        [SettingsUISection(kTabKeys, kGroupKeysReset)]
        [SettingsUIButton]
        [SettingsUIConfirmation(kResetConfirmKey, kResetConfirmText)]
        public bool ResetAllSettings
        {
            get { return false; }     // 按钮没有状态；这个 getter 只为「按 getter 取值的调用方不炸」而存在
            set
            {
                if (!value) return;     // 游戏只在点击时递 true；递 false（含存档读回来的那份）一律无视
                ResetEverythingToDefaults();
            }
        }

        // —————————————————————————— 关于（第八轮反馈 1：版本 / 作者 / 三颗跳转按钮）
        //
        // 两行只读文本用的是游戏那条「string + 只有 getter ⇒ StringField」的分支
        //（FACT：Game.UI.Menu/AutomaticSettings.cs:1109-1116，控件建出来是 LocalizedValueField，
        //  accessor 的 setter 是**空函数**：FACT :1404-1421 ⇒ 玩家看得见、改不了，正是「展示」要的语义）。
        // 注意 :1414 那句是 `LocalizedString.Value((string) GetValue(...))` ⇒ **绝不能返回 null**。
        //
        // 三颗跳转按钮走 `Application.OpenURL`（游戏自己的「其他链接」就是这么开的：
        // FACT：Game.UI.Menu/ParadoxBindings.cs:694-709 —— 隐私政策那两条走平台 SDK，落到 else 就是 OpenURL）。
        // 三颗挂在同一个 [SettingsUIButtonGroup] 下 ⇒ 游戏把三颗 Button 塞进同一行 ButtonRow
        //（FACT：AutomaticSettings.cs:718-733 GetButtonsGroup + :1239 取特性上的组名；
        //  范本就是游戏自己的三颗工具链按钮 ModdingSettings.cs:36/55/74 同用 "toolchainAction"）。
        // ⚠ 组名那张表是 **static 且全局**的（:709/:715），别的模组也叫 "AboutLinks" 就会跟我们并成一行
        //   ⇒ 组名带上前缀，与游戏默认组名（声明类型名 + 属性名）同样唯一。

        [SettingsUISection(kTabKeys, kGroupAbout)]
        public string ModVersionText { get { return ZoneSnapperMod.kVersion; } }

        [SettingsUISection(kTabKeys, kGroupAbout)]
        public string AuthorNameText { get { return kAuthor; } }

        [SettingsUISection(kTabKeys, kGroupAbout)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(kAboutButtonGroup)]
        public bool OpenKoFiLink
        {
            set { if (value) OpenUrl(kUrlKoFi, "Ko-fi"); }
        }

        [SettingsUISection(kTabKeys, kGroupAbout)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(kAboutButtonGroup)]
        public bool OpenForumLink
        {
            set { if (value) OpenUrl(kUrlForum, "Paradox Forum"); }
        }

        [SettingsUISection(kTabKeys, kGroupAbout)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(kAboutButtonGroup)]
        public bool OpenRainbowLink
        {
            set { if (value) OpenUrl(kUrlRainbow, "RAINBOW"); }
        }

        /// <summary>开外链：游戏用的是同一个入口，但它会起浏览器 ⇒ 失败只记一行，绝不让选项页卡住。</summary>
        private static void OpenUrl(string url, string label)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return;
                UnityEngine.Application.OpenURL(url);
                SnapperLog.Info("[ZoneSnapper] 关于页已打开链接：" + label);
            }
            catch (Exception e)
            {
                SnapperLog.Warn("打开链接失败（" + label + "）：" + e.GetType().Name + " " + e.Message);
            }
        }

        // —————————————————————————— 条件与同步

        /// <summary>给 HideByCondition 用：条件方法返回 true 时隐藏该行。</summary>
        public bool NotEnabled() { return !m_Enabled; }

        public bool NoDistrict() { return !m_Enabled || !SnapDistrict; }
        public bool NoLot() { return !m_Enabled || !SnapLot; }
        public bool NoSurface() { return !m_Enabled || !SnapSurface; }
        public bool NoMapTile() { return !m_Enabled || !SnapMapTile; }
        public bool NoFollow() { return !m_Enabled || !FollowCommitted; }

        /// <summary>淡出程度这一行只在「开预览」时才有意义。</summary>
        public bool NoPreview() { return !m_Enabled || !ShowLivePreview; }

        /// <summary>把界面值整体翻译成一个新的 ModConfig 换进去（换对象而不是逐字段改，避免读到半套新值）。</summary>
        public void Sync()
        {
            m_cfgStamp = Stamp();
            ModConfig cfg = new ModConfig();
            cfg.Enabled = m_Enabled;
            cfg.SnapDistrict = SnapDistrict;
            cfg.SnapLot = SnapLot;
            cfg.SnapSurface = SnapSurface;
            cfg.SnapMapTile = SnapMapTile;

            cfg.DistrictSnapDistance = m_DistrictSnapPercent / 100.0;
            cfg.LotSnapDistance = m_LotSnapPercent / 100.0;
            cfg.SurfaceSnapDistance = m_SurfaceSnapPercent / 100.0;
            cfg.MapTileSnapDistance = m_MapTileSnapPercent / 100.0;

            cfg.Mode = SnapMode;
            cfg.CurveDetail = m_CurveDetailPercent / 100.0;
            cfg.NodeContinuation = NodeContinuation;

            cfg.FollowCommittedAreas = FollowCommitted;
            cfg.HighlightFollowedAreas = HighlightFollowed;

            cfg.ShowLivePreview = ShowLivePreview;
            cfg.PreviewAlpha = m_PreviewFadePercent / 100.0;

            SnapperState.Config = cfg;
            SnapperState.Enabled = m_Enabled;
        }

        // 第六轮反馈 3 的"滑杆不管用"：实机日志里 radius 恒等于 prefab，说明 setter 那条路在实机没走通
        // （UI 写值的方式我们控不住）。改成**每帧对指纹**：指纹变了就整体重Sync一次，
        // 不管值是 setter 写进来的还是反射直接写字段写进来的。指纹计算是十几次整数运算，每帧可忽略。
        private long m_cfgStamp;

        private long Stamp()
        {
            long h = 17;
            h = h * 31 + (m_Enabled ? 1 : 0);
            h = h * 31 + (SnapDistrict ? 1 : 0);
            h = h * 31 + (SnapLot ? 1 : 0);
            h = h * 31 + (SnapSurface ? 1 : 0);
            h = h * 31 + (SnapMapTile ? 1 : 0);
            h = h * 31 + m_DistrictSnapPercent;
            h = h * 31 + m_LotSnapPercent;
            h = h * 31 + m_SurfaceSnapPercent;
            h = h * 31 + m_MapTileSnapPercent;
            h = h * 31 + (int)SnapMode;
            h = h * 31 + m_CurveDetailPercent;
            h = h * 31 + (NodeContinuation ? 1 : 0);
            h = h * 31 + (FollowCommitted ? 1 : 0);
            h = h * 31 + (HighlightFollowed ? 1 : 0);
            h = h * 31 + (ShowLivePreview ? 1 : 0);
            h = h * 31 + m_PreviewFadePercent;
            return h;
        }

        /// <summary>每帧调一次：界面值变了就重Sync（不依赖 setter 是否被 UI 调到）。</summary>
        public void PollSync()
        {
            long s = Stamp();
            if (s != m_cfgStamp) Sync();
        }

        public void AttachSystem(Systems.ZoneSnapperSystem system) { m_system = system; }

        /// <summary>快捷键回调（由 Mod 入口在 onInteraction 里转发）。只认按下沿。</summary>
        public void OnHotkey(string actionName)
        {
            if (actionName == kActionToggleEnabled)
            {
                Enabled = !m_Enabled;
                try { ApplyAndSave(); } catch (Exception e) { SnapperLog.Warn("快捷键落盘失败：" + e.Message); }
                SnapperLog.Info("[ZoneSnapper] 快捷键切换总开关 ⇒ " + (m_Enabled ? "开" : "关"));
            }
            else if (actionName == kActionCycleMode)
            {
                int n = (int)SnapMode + 1;
                if (n > (int)TraceMode.NoNetworkSwitch) n = 0;
                SnapMode = (TraceMode)n;
                Sync();
                try { ApplyAndSave(); } catch (Exception) { }
                SnapperLog.Info("[ZoneSnapper] 贴合模式 ⇒ " + ModeName(SnapMode) + "（" + (int)SnapMode + "/4）");
            }
            else if (actionName == kActionRetraceNow)
            {
                SnapperState.ResetRequested = true;
                SnapperState.FollowRequested = true;
                SnapperLog.Info("[ZoneSnapper] 快捷键：请求重算（下一帧清缓存；视野内的已提交区域按最新路网重描）");
            }
            else if (actionName == kActionUndoFollow)
            {
                SnapperState.UndoFollowRequested = true;
                SnapperLog.Info("[ZoneSnapper] 快捷键：请求退回上一次自动跟随的调整");
            }
        }

        /// <summary>模式名（日志用；面板上的名字走本地化词条，见 BuildLocaleMap 里那五条 TRACEMODE key）。</summary>
        public static string ModeName(TraceMode m)
        {
            switch (m)
            {
                case TraceMode.Shortest: return "路径最短";
                case TraceMode.FewestNodes: return "节点最少";
                case TraceMode.FewestCorners: return "交点最少";
                case TraceMode.NoNetworkSwitch: return "不切换网络";
                default: return "智能";
            }
        }

        /// <summary>
        /// 设置 id 的字面量（= 程序集名 + Mod 类命名空间 + Mod 类名，FACT：ModSetting.cs:36-44）。
        /// 第六轮实机「贴合模式」下拉框显示成 key 的根因就是**下拉选项的 key 少了这一段**：
        /// 游戏拼的是 <c>Options.&lt;id&gt;.TRACEMODE[成员]</c>（AutomaticSettings.cs:846-855 + :906-913），
        /// 我们第五轮注册成了 <c>Options.TRACEMODE[成员]</c> ⇒ 永远查不到 ⇒ key 上屏。
        /// 这里写死一份给静态方法用；CheckTemplatesOnce 会在实机拿 setting.id 与它对账，漂了先打 Warn。
        /// </summary>
        public const string kSettingId = "ZoneSnapper.ZoneSnapper.ZoneSnapperMod";

        /// <summary>
        /// 下拉框选项的 key（反馈 5 加、反馈 6 修）：带 id 前缀的完整模板。
        /// </summary>
        public static string ModeKey(TraceMode m)
        {
            return Engine.LocaleKit.EnumKey(kSettingId, typeof(TraceMode).Name, m.ToString());
        }

        public override void SetDefaults()
        {
            m_Enabled = true;
            Enabled = true;
            SnapDistrict = true;
            SnapLot = true;
            SnapSurface = true;
            SnapMapTile = false;
            DistrictSnapPercent = 100;
            LotSnapPercent = 100;
            SurfaceSnapPercent = 100;
            MapTileSnapPercent = 100;
            SnapMode = TraceMode.Smart;
            CurveDetailPercent = 0;
            NodeContinuation = true;
            FollowCommitted = true;      // 第六轮：写盘只由「重算贴合区域」那一击发起，默认开（关着那颗键就是死的）
            HighlightFollowed = true;
            ShowLivePreview = true;
            PreviewFadePercent = 35;
            Sync();
        }

        /// <summary>
        /// 选项页那颗「重置所有设置项」按钮真正做的事。
        ///
        /// 三步的顺序照游戏自己那颗「全部恢复默认」写：它遍历每个 Setting 依次
        /// <c>SetDefaults()</c> → <c>SetNewPlayerDefaults()</c> → <c>ApplyAndSave()</c>
        /// （FACT：Game.Settings/SharedSettings.cs:121-130 <c>Reset()</c>），
        /// 也就是说"改完立刻落盘"是游戏自己的口径，不是我们新发明的。
        /// 这里没有 <c>SetNewPlayerDefaults()</c> 可覆盖（那是给新玩家开局用的），跳过。
        ///
        /// 快捷键单独清，而且要清**三处**，少一处就是假的：
        ///
        /// ① **属性里那份 ProxyBinding** —— 它是**结构体**（FACT：Game.Input/ProxyBinding.cs:12
        ///    <c>public struct ProxyBinding</c>）。`binding.path = ""` 改的是取属性时那份副本，
        ///    改完就丢 ⇒ 必须把改过的副本**赋回属性**。（游戏自己就是这个写法：同文件 :714-718
        ///    <c>WithPath</c> 先改再 <c>return this</c>；ModSetting.cs:213-216 的 watcher 也是
        ///    <c>property.SetValue(this, newBinding)</c> 整个塞回去。）
        /// ② **输入层那份真绑定** —— 动作早已 <c>AddActions</c> 进映射表，只改存档不动映射表，
        ///    那颗键在**本次会话里照样能触发**。走游戏自己的入口
        ///    <c>InputManager.instance.SetBinding(copy, out _)</c>，副本的 path 置空、modifiers 清空，
        ///    口径照抄游戏解绑冲突时的那三行（FACT：Game.UI.Menu/InputRebindingUISystem.cs:687-691
        ///    <c>resolution2.path = string.Empty; resolution2.modifiers = Array.Empty&lt;ProxyModifier&gt;()</c>）。
        ///    空 path 不会被拒：SetBindingImpl 只在 <c>!canBeEmpty</c> 时才拒绝空值
        ///    （FACT：Game.Input/InputManager.cs:997-1001），而模组的 composite 没被改过这项，
        ///    默认就是 true（FACT：Game.Input/CompositeInstance.cs:14、:88-97）。
        ///    <c>SetBinding</c> 内部 <c>action.Update()</c> 会触发 watcher ⇒ 游戏顺手把我们 ①里那份
        ///    再刷一次成有效值（FACT：ModSetting.cs:210-216 + ProxyBinding.cs:197-209），两头自然对齐。
        /// ③ **动作的开关** —— <c>shouldBeEnabled</c> 只在 OnLoad 时按「有没有绑到键」算过一次
        ///    （ZoneSnapperMod.cs:263-271）。清空绑定后如果不把它摁回 false，就是一颗
        ///    <c>DeviceType.All</c> 的激活器挂在没有 path 的动作上 ⇒ **按任意键都触发本模组**，
        ///    正是 Playbook §3.2 记着的那次「幻影触发」事故。
        ///    （FACT：Game.Input/ProxyAction.cs:322-347，set true 时 <c>new InputActivator(..., DeviceType.All)</c>；
        ///     模组动作不是 builtIn，所以这一支不会走 :334-337 那条「内置动作不许直接开」的抛错。）
        ///
        /// 清的时候**只清 path 与 modifiers，不动 mapName / actionName / device / component**：
        /// 注册快捷键时游戏拿这几个字段拼出动作（FACT：Game.Modding/ModSetting.cs:157-164
        /// <c>item.m_Map = binding.mapName; item.m_Name = binding.actionName; ...</c>），
        /// 再按 <c>binding.device</c> 分派 composite（:171）。整个结构体换成 <c>default</c> 就是把这些一起抹了
        /// ⇒ 那颗动作**下一局根本注册不上**（表现是"按了没反应"，最难查的那种）。
        /// 空 path 才是游戏自己表示"未绑定"的方式：游戏给我们生成的那一份默认就是空 path
        ///（FACT：同文件 :57-64 <c>InitializeKeyBindings</c> + :245-248 <c>CreateBinding</c> 里
        ///  <c>result2.path = control</c>，而 <c>control</c> 对 <c>BindingKeyboard.None</c> 就是
        ///  <c>string.Empty</c>：FACT：Game.Settings/SettingsUIKeyboardBindingAttribute.cs:18-20）。
        /// 结构体也没有 null 这一说 ⇒ 这里连判空都不需要。
        /// </summary>
        public void ResetEverythingToDefaults()
        {
            try
            {
                m_resetCleared = 0;
                m_resetMapOk = 0;
                ToggleEnabledBinding = Unbind(ToggleEnabledBinding, kActionToggleEnabled);
                CycleModeBinding = Unbind(CycleModeBinding, kActionCycleMode);
                RetraceNowBinding = Unbind(RetraceNowBinding, kActionRetraceNow);
                UndoFollowBinding = Unbind(UndoFollowBinding, kActionUndoFollow);

                SetDefaults();            // 末尾自带 Sync() ⇒ 引擎那份 ModConfig 立刻跟上

                // 会话缓存必须一起丢：玩家可能正画到一半，旧栈里的半径/模式是按老设置算的。
                try { m_system?.ForceReset(); } catch (Exception) { }

                try { ApplyAndSave(); }
                catch (Exception e) { SnapperLog.Warn("重置后落盘失败：" + e.Message); }

                // mapOk 是 ② 的账：SetBinding 说「没改成」的那几颗，本次会话里映射表还挂在老键上，
                // 只能靠重启兜住（③ 已经把它摁成不触发，所以不会误动，但选项页显示可能还是老键名）。
                // cleared 只用于对照「四颗都走过了」——它不等于 mapOk，两个数不一样就是 ② 有颗键没落进映射表。
                SnapperLog.Info(string.Format(
                    "[ZoneSnapper] 设置已重置：所有选项回到默认值，四颗快捷键已清空（需要重新配）cleared={0}/4 mapOk={1}",
                    m_resetCleared, m_resetMapOk));
            }
            catch (Exception e)
            {
                // 这颗按钮做的都是"往回退"的动作，失败时保持现状即可，绝不能把面板卡住。
                SnapperLog.Error("[ZoneSnapper] 重置失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        private int m_resetCleared;
        private int m_resetMapOk;

        /// <summary>
        /// 解一颗键：改副本 → 写映射表 → 把没绑上键的动作摁回 shouldBeEnabled=false → 返回副本给属性。
        /// 顺序有意：先让游戏把有效值算出来（SetBinding 里的 Update 会回调 watcher 刷属性），
        /// 再返回副本，让调用方那一句赋值盖在最后一次有效值上；两边的 path 都是空，谁后到都一样。
        /// </summary>
        private ProxyBinding Unbind(ProxyBinding binding, string actionName)
        {
            ProxyBinding cleared = binding.Copy();
            cleared.path = string.Empty;
            cleared.modifiers = Array.Empty<ProxyModifier>();
            m_resetCleared++;

            try
            {
                if (Game.Input.InputManager.instance != null
                    && Game.Input.InputManager.instance.SetBinding(cleared, out _))
                {
                    m_resetMapOk++;
                }
            }
            catch (Exception e)
            {
                SnapperLog.Warn("解绑写回映射表失败（" + actionName + "）：" + e.GetType().Name);
            }

            // ③：这颗动作现在到底还有没有绑到可用的键？没有就必须关掉激活器（幻影触发那一条）。
            try
            {
                ProxyAction action = GetAction(actionName);
                if (action != null && !AnyBindableBinding(action))
                {
                    action.shouldBeEnabled = false;
                }
            }
            catch (Exception e)
            {
                SnapperLog.Warn("关掉动作开关失败（" + actionName + "）：" + e.GetType().Name);
            }
            return cleared;
        }

        /// <summary>判据与 ZoneSnapperMod.HasBindableBinding 同源：只要还剩一颗能用的键就别关开关。</summary>
        private static bool AnyBindableBinding(ProxyAction action)
        {
            try
            {
                foreach (ProxyBinding b in action.bindings)
                {
                    if (Engine.InputKit.IsBindablePath(b.path)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // —————————————————————————— 多语言
        //
        // 【为什么整段重写】实机 v0.1.0/0.1.1 玩家看到满屏的 key 名而不是文字。根因不是「翻译没写」，
        // 是**我们注册的词条名根本不是游戏查询的那个 key**：
        //   游戏建选项行：path = id + "." + 声明类型名 + "." + 属性名，
        //                 标题查 "Options.OPTION[path]"、说明查 "Options.OPTION_DESCRIPTION[path]"
        //                 （FACT：Game.UI.Menu/AutomaticSettings.cs:327-339 GetPath、:341-368 GetDisplayName/GetDescription）
        //   页标题/标签页/分组/按键映射：见 Game.Modding/ModSetting.cs:303-371 六个方法
        //   查不到 ⇒ UILocalizationManager.Translate 直接 data.Set(key) ⇒ key 原样上屏
        //                 （FACT：Game.UI.Localization/UILocalizationManager.cs:19-29）
        //
        // 【这一版怎么保证不再犯】
        //  ① key 一律**调用 ModSetting 自己的方法**取，绝不手写字面量（模板变了跟着变）；
        //  ② 行由反射枚举带 [SettingsUISection] 的公开属性 —— 与游戏建行的判据同源；
        //     漏了词条会在日志里点名（MissingSlugs），上屏的是英文而不是 key（LocaleKit 有英文兜底）；
        //  ③ 词条正文搬到 Engine/LocaleKit.cs（纯 BCL），回归壳 L 段钉住
        //     「模板与反编译逐字一致」+「每行 12 种语言都有词」；
        //  ④ 术语按反馈 9：与游戏自己的用词对齐（市辖区 / 专门产业区 / 表面区域 / 城市边界…），
        //     官方各语言原文抄在 LocaleKit 的注释里。

        /// <summary>本次构建里没找到词条的 slug；游戏侧在 OnLoad 末尾把它打进日志。</summary>
        public static readonly List<string> MissingSlugs = new List<string>();

        /// <summary>
        /// 引擎侧硬编码的 key 模板与游戏自己算出来的 key 不一致时，这里写下差异（游戏侧打 Warn）。
        /// </summary>
        public static string TemplateDiff;

        /// <summary>
        /// 游戏当前支持的语言列表。拿不到（早期/异常）时退回 LocaleKit 里那 12 个官方语言。
        /// ⚠ 中文的代码是 <c>zh-HANS</c>（繁体 <c>zh-HANT</c>），不是 zh-CN / zh-TW。
        /// ⚠ 出处更正（v0.2.0 实测）：这里**没有** <c>StreamingAssets/Locale/l10n_*.txt</c> 那种明文文件
        ///   （全盘 maxdepth 4 搜 <c>l10n_*</c> 零命中）。真实的两处凭据是
        ///   ① <c>Cities2_Data/Content/Game/Locale.cok</c> 的成员名就是 <c>zh-HANS.loc</c> / <c>zh-HANT.loc</c>，
        ///   ② 实机日志里的 <c>activeLocaleId=zh-HANS</c>。结论不变，依据变了。
        /// </summary>
        public static string[] SupportedLocales()
        {
            try
            {
                string[] l = GameManager.instance.localizationManager.GetSupportedLocales();
                if (l != null && l.Length > 0) return l;
            }
            catch (Exception) { }
            return LocaleKit.kLocales;
        }

        /// <summary>给某个语言建一份「游戏要的 key → 文案」字典。每份各自构建，不读全局当前语言。</summary>
        public static Dictionary<string, string> BuildLocaleMap(ModSetting setting, string locale)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(160, StringComparer.Ordinal);
            if (setting == null) return dict;

            CheckTemplatesOnce(setting);

            Put(dict, setting, setting.GetSettingsLocaleID(), "mod.name", locale);

            Put(dict, setting, setting.GetOptionTabLocaleID(kTabSnap), "tab.snap", locale);
            Put(dict, setting, setting.GetOptionTabLocaleID(kTabOther), "tab.other", locale);
            Put(dict, setting, setting.GetOptionTabLocaleID(kTabKeys), "tab.keys", locale);

            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupMain), "group.MainSwitch", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupTools), "group.Tools", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupDistance), "group.SnapDistance", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupMode), "group.Mode", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupSpecial), "group.SpecialTargets", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupCurve), "group.CurveDetail", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupContinuation), "group.Continuation", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupFollow), "group.Follow", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupPreview), "group.Preview", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupKeysSnap), "group.KeysSnap", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupKeysArea), "group.KeysArea", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupKeysReset), "group.KeysReset", locale);
            Put(dict, setting, setting.GetOptionGroupLocaleID(kGroupAbout), "group.About", locale);

            // 设置行：反射枚举，判据与游戏一致（带 [SettingsUISection] 的公开属性）。
            System.Reflection.PropertyInfo[] props = typeof(ZoneSnapperSetting).GetProperties(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            for (int i = 0; i < props.Length; i++)
            {
                object[] attrs = props[i].GetCustomAttributes(typeof(SettingsUISectionAttribute), true);
                if (attrs == null || attrs.Length == 0) continue;
                string name = props[i].Name;
                if (!LocaleKit.IsKnownRow(name) && !MissingSlugs.Contains(name + " (不在 LocaleKit.kRows 里)"))
                {
                    MissingSlugs.Add(name + " (不在 LocaleKit.kRows 里)");
                }
                Put(dict, setting, setting.GetOptionLabelLocaleID(name), name, locale);
                Put(dict, setting, setting.GetOptionDescLocaleID(name), name + ".desc", locale);
            }

            // 下拉框的五个选项（枚举成员）。key 模板不是 ModSetting 的方法，只能按游戏源码那条格式拼；
            // 与游戏不一致的后果不是「显示 key」而是「显示英文兜底」，因为 LocalizedString 查不到时
            // 会把 id 当值显示 —— 所以这五行也在 L 段钉住。
            for (int m = 0; m < kModes.Length; m++)
            {
                TraceMode mode = kModes[m];
                string slug = "mode." + mode;
                if (!LocaleKit.IsKnownRow(slug)) MissingSlugs.Add(slug + " (不在 LocaleKit.kRows 里)");
                dict[ModeKey(mode)] = LocaleKit.Text(slug, locale) ?? mode.ToString();
            }

            // 「重置」按钮的确认弹窗文本。游戏查的是 Options.WARNING[<confirmMessageId>]
            //（FACT：AutomaticSettings.cs:1146-1158），与 OPTION / OPTION_DESCRIPTION 不是同一套模板，
            // 反射那一圈循环注册不到它 ⇒ 必须自己补这一行。漏了的后果不是显示 key，
            // 而是退回 IdWithFallback 的第二参数（那句中文兜底），所以实机一眼能看出注册断没断。
            Put(dict, setting, "Options.WARNING[" + kResetConfirmKey + "]", "reset.confirm", locale);

            Put(dict, setting, setting.GetBindingMapLocaleID(), "binding.map", locale);
            return dict;
        }

        private static readonly TraceMode[] kModes =
        {
            TraceMode.Smart, TraceMode.Shortest, TraceMode.FewestNodes,
            TraceMode.FewestCorners, TraceMode.NoNetworkSwitch
        };

        private static bool m_TemplatesChecked;

        /// <summary>
        /// 只跑一次：把引擎里抄来的 key 模板与游戏自己的算法对一遍账。
        /// 游戏改了模板 ⇒ 界面会重新变成满屏 key，这一行 Warn 让我们先于玩家知道。
        /// </summary>
        private static void CheckTemplatesOnce(ModSetting setting)
        {
            if (m_TemplatesChecked) return;
            m_TemplatesChecked = true;
            try
            {
                string id = setting.id;
                string name = setting.name;
                if (!string.Equals(id, kSettingId, StringComparison.Ordinal))
                    TemplateDiff = "设置 id 漂移：" + id + " != " + kSettingId + "（下拉选项的 key 会跟着错）";
                else if (!string.Equals(setting.GetSettingsLocaleID(), LocaleKit.SectionKey(id), StringComparison.Ordinal))
                    TemplateDiff = "页标题：" + setting.GetSettingsLocaleID() + " != " + LocaleKit.SectionKey(id);
                else if (!string.Equals(setting.GetOptionLabelLocaleID("Enabled"), LocaleKit.LabelKey(id, name, "Enabled"), StringComparison.Ordinal))
                    TemplateDiff = "行标题：" + setting.GetOptionLabelLocaleID("Enabled") + " != " + LocaleKit.LabelKey(id, name, "Enabled");
                else if (!string.Equals(setting.GetOptionDescLocaleID("Enabled"), LocaleKit.DescKey(id, name, "Enabled"), StringComparison.Ordinal))
                    TemplateDiff = "行说明：" + setting.GetOptionDescLocaleID("Enabled") + " != " + LocaleKit.DescKey(id, name, "Enabled");
                else if (!string.Equals(setting.GetOptionTabLocaleID(kTabSnap), LocaleKit.TabKey(id, kTabSnap), StringComparison.Ordinal))
                    TemplateDiff = "标签页：" + setting.GetOptionTabLocaleID(kTabSnap) + " != " + LocaleKit.TabKey(id, kTabSnap);
                else if (!string.Equals(setting.GetOptionGroupLocaleID(kGroupMain), LocaleKit.GroupKey(id, kGroupMain), StringComparison.Ordinal))
                    TemplateDiff = "分组：" + setting.GetOptionGroupLocaleID(kGroupMain) + " != " + LocaleKit.GroupKey(id, kGroupMain);
                else if (!string.Equals(setting.GetBindingMapLocaleID(), LocaleKit.BindingMapKey(id), StringComparison.Ordinal))
                    TemplateDiff = "按键映射：" + setting.GetBindingMapLocaleID() + " != " + LocaleKit.BindingMapKey(id);
                else if (!string.Equals(ModeKey(TraceMode.Smart), LocaleKit.EnumKey(id, "TraceMode", "Smart"), StringComparison.Ordinal))
                    TemplateDiff = "下拉选项：" + ModeKey(TraceMode.Smart) + " != " + LocaleKit.EnumKey(id, "TraceMode", "Smart");
                else if (!string.Equals(setting.GetOptionWarningLocaleID("ResetAllSettings"),
                                         "Options.WARNING[" + kResetConfirmKey + "]", StringComparison.Ordinal))
                    TemplateDiff = "确认弹窗：" + setting.GetOptionWarningLocaleID("ResetAllSettings") +
                                   " != Options.WARNING[" + kResetConfirmKey + "]";
            }
            catch (Exception e)
            {
                TemplateDiff = "对账本身失败：" + e.GetType().Name;
            }
        }

        private static void Put(Dictionary<string, string> dict, ModSetting setting, string key, string slug, string locale)
        {
            string text = LocaleKit.Text(slug, locale);
            if (string.IsNullOrEmpty(text))
            {
                // 只在英文那一轮记名（英文列就是「这行存不存在」的判据），免得一个缺失被记 12 次。
                if (string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase) && !MissingSlugs.Contains(slug))
                {
                    MissingSlugs.Add(slug);
                }
                return;
            }
            dict[key] = text;
        }
    }

    /// <summary>官方框架的字典源：每个 locale 各建一份（见上面「不读全局 activeLocale」那条硬口径）。</summary>
    internal sealed class ZoneSnapperLocaleSource : IDictionarySource
    {
        private readonly Dictionary<string, string> m_Entries;

        public ZoneSnapperLocaleSource(ModSetting setting, string locale)
        {
            m_Entries = ZoneSnapperSetting.BuildLocaleMap(setting, locale);
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return m_Entries;
        }

        public void Unload() { }
    }
}
