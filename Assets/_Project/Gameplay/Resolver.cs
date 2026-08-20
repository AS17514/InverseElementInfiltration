using System.Collections.Generic;
using TheLaw.Core;
using TheLaw.Data;
using UnityEngine;

namespace TheLaw.Gameplay
{
    /// <summary>
    /// 结算器（统一落账器）——GameState 唯一写入口 ★
    /// 所有状态修改经此：落账 → 发事件（通知）→ LogAction（回放记录）。
    /// 触发点动作走"待执行队列"（防重入：守卫退出后由 BattleFlow 统一取走执行）。
    /// </summary>
    public class Resolver
    {
        private readonly GameState _state;
        private readonly BoardRules _boardRules;

        public Resolver(GameState state, BoardRules boardRules)
        {
            _state = state;
            _boardRules = boardRules;
        }

        // ========== 落账入口 ==========

        public void ResolveAll(List<ConcreteAction> actions)
        {
            foreach (var action in actions)
            {
                Resolve(action);
            }
        }

        public void Resolve(ConcreteAction action)
        {
            LogAction(action);
            switch (action)
            {
                case MoveAction move:
                    ResolveMove(move);
                    break;
                case AttackAction attack:
                    ResolveAttack(attack);
                    break;
                case DeployAction deploy:
                    ResolveDeploy(deploy);
                    break;
                case PromoteAction promote:
                    ResolvePromote(promote);
                    break;
                case SkipAction skip:
                    ResolveSkip(skip);
                    break;
            }
        }

        // ========== 各类落账 ==========

        private void ResolveMove(MoveAction action)
        {
            var piece = _state.GetPiece(action.pieceId);
            if (piece == null)
            {
                return;
            }
            _state.Pieces.Remove(action.from);
            piece.position = action.to;
            _state.Pieces[action.to] = piece;
            EventCenter.Instance.EventTrigger(GameEvent.PieceMoved, new MoveInfo { PieceId = piece.Id, From = action.from, To = action.to });
        }

        private void ResolveAttack(AttackAction action)
        {
            var piece = _state.GetPiece(action.pieceId);
            if (piece == null)
            {
                return;
            }
            int damage = _boardRules.GetAttackDamage(_state, piece, action.template);

            if (action.template.mode == AttackMode.MeleeAOE)
            {
                // 近战群攻：范围内全部格子被攻击（玩家选一格仅作确认）
                ResolveMeleeAOE(piece, action, damage);
                return;
            }

            // 目标格结算（空格 = 空放：无伤害仍耗槽）
            var target = _state.GetPieceAt(action.targetCell);
            int targetId = target != null ? target.Id : -1; // 死亡前记录——HandleDeath 会从 Pieces 移除
            bool died = false;
            bool elementHit = false; // 属性玩法触发（相克击败/相生无伤——表现区分用）
            if (target != null)
            {
                // ⚠️ 2026-08-20「属性」玩法：攻击命中按属性判定相克/相生（仅双方棋子且双方有属性——麻将牌/无属性不判）
                if (_state.IsStyleActive("element") && piece.element != Element.None && target.element != Element.None)
                {
                    if (ElementRules.IsCountering(piece.element, target.element))
                    {
                        // 相克：直接击败（无视护盾/抗性——伤害打穿护盾+承伤）+ 基础得分 + 棋盘上同属性棋子数量
                        int bypass = target.durability + target.shieldCount + 1;
                        died = ModifyDurability(target, -bypass, piece);
                        _state.BaseScore += CountSameElementOnBoard(piece.element);
                        elementHit = true;
                    }
                    else if (ElementRules.IsGenerating(piece.element, target.element))
                    {
                        // 相生：不造成任何伤害 + 获得目标复制牌入手牌（属性相同）
                        _state.Hand.Add(Card.Piece(target.DefId, target.element));
                        EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
                        elementHit = true;
                        // died 保持 false（无伤害）
                    }
                    else
                    {
                        died = ModifyDurability(target, -damage, piece); // 无关：正常伤害
                    }
                }
                else
                {
                    died = ModifyDurability(target, -damage, piece); // killer = 攻击者
                }
            }
            else if (_state.IsStyleActive(Mahjong.StyleId) && _state.MahjongWalls.ContainsKey(action.targetCell))
            {
                // ⚠️ 2026-08-20 麻将：攻击命中墙体格（无棋子目标）——选了即破坏（整墙 + 填牌山点数 + 基础分 +1）
                BreakMahjongWall(action.targetCell);
            }

            // 附着结算（Attach/OnAttack：如十字额外伤害——范围额外结算）
            ResolveAttachOnAttack(piece, action.targetCell, action.template, elementHit ? 0 : damage);

            // 发事件（UI 表现依据：攻击者/目标/伤害/是否死亡）
            EventCenter.Instance.EventTrigger(GameEvent.DamageDealt, new DamageInfo
            {
                AttackerId = piece.Id,
                TargetId = targetId,
                TargetCell = action.targetCell,
                Damage = elementHit && !died ? 0 : damage, // 相生无伤（Damage=0——前端"柔"表现）；相克改用实际（已死——Damage 仍显示原数值）
                TargetDied = died,
                FriendlyFire = action.template.friendlyFire,
            });
        }

        /// <summary>棋盘上同属性棋子数量（2026-08-20 相克击杀得分用——"棋盘上与攻击棋子属性相同的棋子数量"，含攻击者自身）。</summary>
        private int CountSameElementOnBoard(Element element)
        {
            int count = 0;
            foreach (var p in _state.Pieces.Values)
            {
                if (p != null && p.element == element) count++;
            }
            return count;
        }

        /// <summary>
        /// 近战群攻结算：范围内全部格子（有棋子的都扣承伤，友伤按模板）。
        /// 逐目标发 DamageDealt（后端待办 #6）：每个命中目标带自己的 TargetId——UI 按 TargetId 闪白
        /// （与 ResolveAttack 单目标同契约）；多目标并行表现由 UI"组内并行"落实（见 docs/前端协作事项-表现层组内并行.md）。
        /// ⚠️ 空放（范围有格但无命中）：必须补发空放事件（TargetId=-1）——否则 UI 无表现、规则层永远等表现完成卡死。
        /// </summary>
        private void ResolveMeleeAOE(PieceInstance attacker, AttackAction action, int damage)
        {
            var cells = _boardRules.GetAttackableCells(_state, attacker, action.template);
            bool anyHit = false;
            foreach (var cell in cells)
            {
                var victim = _state.GetPieceAt(cell);
                if (victim != null && (victim.side != attacker.side || action.template.friendlyFire))
                {
                    bool died = ModifyDurability(victim, -damage, attacker);
                    anyHit = true;
                    EventCenter.Instance.EventTrigger(GameEvent.DamageDealt, new DamageInfo
                    {
                        AttackerId = attacker.Id,
                        TargetId = victim.Id,
                        TargetCell = cell,
                        Damage = damage,
                        TargetDied = died,
                        FriendlyFire = action.template.friendlyFire,
                    });
                }
            }
            if (!anyHit)
            {
                // 空放（无命中）：与单目标空放同语义（TargetId=-1——UI 播放攻击者挥空动画 → 表现完成）
                EventCenter.Instance.EventTrigger(GameEvent.DamageDealt, new DamageInfo
                {
                    AttackerId = attacker.Id,
                    TargetId = -1,
                    TargetCell = action.targetCell,
                    Damage = damage,
                    TargetDied = false,
                    FriendlyFire = action.template.friendlyFire,
                });
            }
        }

        private void ResolveAttachOnAttack(PieceInstance attacker, Vector2Int targetCell, AttackTemplate template, int mainDamage)
        {
            foreach (var ability in attacker.GetAllAbilities())
            {
                if (ability.type != SpecialAbilityType.Attach || ability.attachPoint != AttachPoint.OnAttack)
                {
                    continue;
                }
                // 范围额外结算：十字 = 目标格 + 上下左右共 5 格
                // ⚠️ 排除中心格（targetCell 自身）——它已在主攻击中结算（避免双重伤害）
                var cells = GetAttachCells(targetCell, ability.attachShape);
                int attachDamage = ability.attachDamage > 0 ? ability.attachDamage : mainDamage;
                foreach (var cell in cells)
                {
                    if (cell == targetCell)
                    {
                        continue; // 中心格 = 主目标，只吃主伤害
                    }
                    var victim = _state.GetPieceAt(cell);
                    if (victim != null && (victim.side != attacker.side || template.friendlyFire))
                    {
                        ModifyDurability(victim, -attachDamage, attacker); // killer = 攻击者（附着结算）
                    }
                }
            }
        }

        private List<Vector2Int> GetAttachCells(Vector2Int center, AttackShape shape)
        {
            var cells = new List<Vector2Int>();
            if (shape == AttackShape.Cross)
            {
                cells.Add(center);
                cells.Add(center + Vector2Int.up);
                cells.Add(center + Vector2Int.down);
                cells.Add(center + Vector2Int.left);
                cells.Add(center + Vector2Int.right);
            }
            else
            {
                cells.Add(center);
            }
            return cells;
        }

        private void ResolveDeploy(DeployAction action)
        {
            var def = ConfigTable.Get<PieceDef>(action.pieceDefId);
            if (def == null)
            {
                return;
            }
            if (_state.Pieces.ContainsKey(action.cell))
            {
                // 防御：部署格被占用拒绝（正常路径 FindDeployCell/IsValidDeployCell 已保证空格——防未来路径绕过检查覆盖棋盘）
                Debug.LogWarning($"[Resolver] 部署格被占用，拒绝：cell={action.cell}（{def.name}）");
                return;
            }
            var piece = new PieceInstance(def, action.side, action.cell)
            {
                Id = _state.AllocatePieceId(),
                waveIndex = action.waveIndex, // 波次标（每波得分累计用）
            };
            // ⚠️ 2026-08-20「属性」玩法：创建时分配属性（一方应含双方棋子——部署/波次都经此）
            // 复制牌（Card 带属性，"属性相同"）优先用牌属性；否则激活玩法时随机（金木水火土）
            if (_state.IsStyleActive("element"))
            {
                piece.element = CardElementFromHand(action.pieceDefId);
                if (piece.element == Element.None)
                {
                    piece.element = RandomElement();
                }
            }
            if (action.side == Side.Player)
            {
                _state.Hand.RemoveAll(c => c.IsPiece && c.defId == action.pieceDefId); // 玩家部署：手牌打出（棋子牌）
            }
            _state.Pieces[action.cell] = piece;
            _state.PiecesById[piece.Id] = piece;
            EventCenter.Instance.EventTrigger(GameEvent.PieceDeployed, new DeployInfo { PieceId = piece.Id, DefId = action.pieceDefId, Side = action.side, Cell = action.cell });
        }

        /// <summary>手牌中该 defId 的带属性牌（复制牌"属性相同"）——有则返回牌属性，无则 None（2026-08-20）。</summary>
        private Element CardElementFromHand(int defId)
        {
            foreach (var card in _state.Hand)
            {
                if (card.IsPiece && card.defId == defId && card.element != Element.None)
                {
                    return card.element;
                }
            }
            return Element.None;
        }

        /// <summary>随机属性（金木水火土——RandomManager 种子相关可复现；属性玩法激活时用）。</summary>
        private static Element RandomElement()
        {
            return (Element)(RandomManager.Instance.Range(1, 6)); // 1=Gold ~ 5=Earth（0=None 跳过）
        }

        private void ResolvePromote(PromoteAction action)
        {
            var piece = _state.GetPiece(action.pieceId);
            if (piece == null)
            {
                return;
            }
            var newDef = ConfigTable.Get<PieceDef>(action.newDefId);
            var oldDefId = piece.DefId;      // 升变前 def（相生复制牌用——被升变棋子）
            var oldElement = piece.element;  // 升变前属性（相克/相生判定用——旧棋子 vs 新棋子）
            piece.def = newDef;
            piece.ApplyDefProperties(); // 承伤+护盾按新身体重算（2026-08-12：护盾此前漏算——升变丢新身体护盾；统一初始化路径）
            // ⚠️ 2026-08-20「属性」玩法：升变 = 新身体 → 属性重随机（激活玩法时）；
            // 相克/相生判定（升变棋子 vs 被升变棋子属性）：
            //   相克 → 倍率 +1（当回合——结算后复位 1）；相生 → 被升变棋子的复制牌入手牌（属性 = 旧属性）
            if (_state.IsStyleActive("element"))
            {
                piece.element = RandomElement();
                if (ElementRules.IsCountering(piece.element, oldElement))
                {
                    _state.ScoreMultiplier += 1; // 升变相克：倍率 +1
                }
                else if (ElementRules.IsGenerating(piece.element, oldElement))
                {
                    _state.Hand.Add(Card.Piece(oldDefId, oldElement)); // 复制牌：属性 = 旧属性
                    EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
                }
            }
            if (piece.side == Side.Player)
            {
                _state.Hand.RemoveAll(c => c.IsPiece && c.defId == action.newDefId); // 升变牌打出（手牌减一）——仅玩家（敌方无手牌）
            }
            EventCenter.Instance.EventTrigger(GameEvent.PiecePromoted, new PromoteInfo { PieceId = piece.Id, NewDefId = action.newDefId });
        }

        private void ResolveSkip(SkipAction action)
        {
            // 无状态变化；Skip 无表现，不进入表现等待（BattleFlow 判定）
        }

        // ========== 承伤统一入口（伤害负、恢复正）==========

        /// <summary>
        /// 承伤±N：归 0 → 死亡流程（killer=击杀者，可为 null——非攻击击杀）。返回是否死亡。
        /// 伤害（delta&lt;0）先经护盾拦截：吸收 min(剩余护盾, 伤害值)，剩余伤害继续扣承伤。
        /// </summary>
        public bool ModifyDurability(PieceInstance piece, int delta, PieceInstance killer = null)
        {
            if (delta < 0 && piece.shieldCount > 0)
            {
                int absorbed = Mathf.Min(piece.shieldCount, -delta); // 护盾抵挡（一次性，不恢复）
                piece.shieldCount -= absorbed;
                if (absorbed > 0)
                {
                    EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, piece.Id); // buff 变化 → UI 刷新护盾标记
                }
                delta += absorbed;
                if (delta >= 0)
                {
                    return false; // 伤害被完全抵挡——承伤不变
                }
            }
            piece.durability += delta;
            if (piece.durability <= 0)
            {
                HandleDeath(piece, killer);
                return true;
            }
            return false;
        }

        // ========== 死亡 ==========

        private void HandleDeath(PieceInstance victim, PieceInstance killer)
        {
            _state.Pieces.Remove(victim.position);
            _state.PiecesById.Remove(victim.Id);

            // 击杀积分（价值分——2026-08-15 改走推导：价值 = 生效程序槽位总和，编辑后随程序变化）
            // + 墓地（仅玩家棋子；敌方无手牌概念）
            // ⚠️ 2026-08-19 计分规则：击败敌方棋子 → **基础得分** + 该棋子价值（固定得分规则）；
            // 总得分/波次得分由回合结算（SettleScore）统一入账（原击杀直加 PlayerScore/WaveScores 移除）
            int killValue = PieceValue.SumValue(victim.GetProgram(_state));
            if (victim.side == Side.Player)
            {
                _state.Graveyard.Add(victim.DefId);
                // ⚠️ EnemyScore：**无策划依据的遗留实现**（2026-08-19 确认保留——仅结算面板显示"敌方得分"，不参与任何判定；
                // 玩家计分规则（回合计分）只有玩家侧——见 计分规则_策划口述_20260819.md）
                _state.EnemyScore += killValue;
                // ⚠️ 2026-08-20 敌方击杀扣分（策划口述——第 3/4 关可用，按关开关）：我方棋子被**敌方棋子**击败 → 本关总得分 - 该棋子价值
                if (killer != null && killer.side == Side.Enemy && (_state.CurrentFloorConfig?.scoreDeductEnabled ?? false))
                {
                    DeductScoreForPlayerLose(victim);
                }
            }
            else
            {
                _state.BaseScore += killValue; // 基础得分积累（回合结束按 基础分×倍率 结算）
                // ⚠️ 2026-08-20 麻将玩法：敌方棋子被击败 → 该牌价值数字填入牌山（刻/顺判定——番数）
                if (_state.IsStyleActive(Mahjong.StyleId))
                {
                    PushMahjongScore(killValue);
                }
            }

            // OnKill 触发点（层差异 + 遗物 + 特殊能力——动作进待执行队列）
            OnKillTriggers(victim, killer);

            EventCenter.Instance.EventTrigger(GameEvent.PieceDied, new DeathInfo { PieceId = victim.Id, Side = victim.side, KillerId = killer != null ? killer.Id : -1 });
        }

        // ========== 麻将玩法（2026-08-20——落账唯一入口）==========

        /// <summary>
        /// 麻将：数字填入牌山（破坏/摸切/敌方棋子被击败）。
        /// 牌山 ≤2：第 3 个填入时**先判定**（在移除之前）——3 个组成刻子/顺子 → 番数 +1、清空牌山；不组 → 移除最早（牌山回到 ≤2）。
        /// </summary>
        public void PushMahjongScore(int value)
        {
            if (value <= 0) return;
            _state.MahjongScore.Add(value);
            if (_state.MahjongScore.Count >= 3)
            {
                if (Mahjong.IsTripletOrSequence(_state.MahjongScore))
                {
                    _state.FanCount += 1;      // 刻子/顺子成形 → 番数 +1
                    _state.MahjongScore.Clear();
                }
                else
                {
                    _state.MahjongScore.RemoveAt(0); // 不组 → 移出最早填入的（牌山保持 ≤2）
                }
            }
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "mahjong-score"); // 牌山/番数变化（前端刷新）
        }

        /// <summary>
        /// 麻将：打出墙体（耗 AP——BattleFlow 校验；1×2 竖＝(x,y)+(x,y+1) 两格，非敌方部署区/空/无墙）。
        /// 手牌移除该麻将牌；两格各记点数（MahjongWalls——阻挡/破坏用）。
        /// </summary>
        public bool PlayMahjongWall(Card mahjongCard, Vector2Int cell)
        {
            if (!mahjongCard.IsMahjong) return false;
            var second = cell + Vector2Int.down; // 竖 = 纵向（dy+1——y 轴向下为正：Unity 坐标 y+ 向下？——现有棋盘 x=列 y=行，上=+y？——统一：竖 = (x,y)+(x,y+1)）
            if (_state.Pieces.ContainsKey(cell) || _state.Pieces.ContainsKey(second)
                || _state.MahjongWalls.ContainsKey(cell) || _state.MahjongWalls.ContainsKey(second)
                || IsEnemyDeployZone(cell) || IsEnemyDeployZone(second))
            {
                return false;
            }
            _state.MahjongWalls[cell] = new ObstacleData(mahjongCard.value);
            _state.MahjongWalls[second] = new ObstacleData(mahjongCard.value);
            RemoveOneMahjongFromHand(mahjongCard.value); // 手牌移除该麻将牌（同点数多张——只移除一张）
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "mahjong-wall");
            return true;
        }

        /// <summary>敌方部署区判定（最上 2 行 = dy 7/6？——按现有部署区：见 BattleFlow.IsValidDeployCell——简化：敌方上半区拒绝墙）。</summary>
        private static bool IsEnemyDeployZone(Vector2Int cell)
        {
            return cell.y >= 6; // 布局：y 越大越靠"前方"（敌方部署区最上 2 行）——与 BattleFlow 敌方部署区一致
        }

        /// <summary>
        /// 麻将：墙体被破坏（攻击命中墙体格——选了即破坏 → 移除整墙（两格）→ 填牌山点数 + 基础得分 +1）。
        /// </summary>
        public void BreakMahjongWall(Vector2Int cell)
        {
            if (!_state.MahjongWalls.TryGetValue(cell, out var wall))
            {
                return;
            }
            // 移除整墙（两格——竖墙配对：同列上下格）
            var second = _state.MahjongWalls.ContainsKey(cell + Vector2Int.down)
                ? cell + Vector2Int.down
                : cell + Vector2Int.up;
            _state.MahjongWalls.Remove(cell);
            _state.MahjongWalls.Remove(second);
            PushMahjongScore(wall.value);       // 破坏 → 填牌山点数
            _state.BaseScore += 1;              // 破坏 → 基础得分 +1
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "mahjong-wall");
        }

        /// <summary>
        /// 麻将：摸切（手牌行动）——手牌麻将牌填入牌山 + 抽一张牌。
        /// 抽一张 = 从抽牌堆（DrawCard）；手牌移除该麻将。
        /// </summary>
        public void MochiCut(Card mahjongCard)
        {
            if (!mahjongCard.IsMahjong) return;
            RemoveOneMahjongFromHand(mahjongCard.value);
            PushMahjongScore(mahjongCard.value);
            DrawCard();
        }

        /// <summary>手牌移除一张指定点数的麻将牌（Card 值语义——同点数多张只去一张）。</summary>
        private void RemoveOneMahjongFromHand(int value)
        {
            int idx = _state.Hand.FindIndex(c => c.IsMahjong && c.value == value);
            if (idx >= 0) _state.Hand.RemoveAt(idx);
        }

        /// <summary>
        /// 麻将：和牌（手牌行动——1 AP）——条件（BattleFlow 校验）：手牌存在雀头（任意两牌价值相同——不限麻将牌）且番数 &gt; 0。
        /// 效果：本回合倍率 + 番数、番数清零。
        /// 返回是否成功（触发了和牌）。
        /// </summary>
        public bool Hu(int fanToUse)
        {
            if (fanToUse <= 0) return false;
            _state.ScoreMultiplier += fanToUse; // 倍率 + 番数（当回合——结算后复位 1）
            _state.FanCount = 0;                // 番数清零
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "mahjong-hu");
            return true;
        }

        /// <summary>
        /// 回合结算（2026-08-19 计分规则——落账唯一入口；BattleFlow.CheckVictory 开头统一调用——3 入口兜底）：
        /// 得分 = 基础分 × 倍率 → 加算总得分（本关）+ 当前波次统计 → 基础分清零、倍率复位 1。
        /// 幂等：基础分 0 且倍率 1（无分可结）→ 跳过（不发事件——CheckVictory 同帧多入口不重复结算）。
        /// 波次索引防御：waveIndex 未开始/越界 → 只累总得分、跳过波次记账。
        /// </summary>
        public void SettleScore(int waveIndex)
        {
            if (_state.BaseScore == 0 && _state.ScoreMultiplier == 1)
            {
                return; // 无分可结（幂等——防 CheckVictory 同帧多入口重复结算）
            }
            int gained = _state.BaseScore * _state.ScoreMultiplier;
            _state.PlayerScore += gained;
            if (waveIndex >= 0 && waveIndex < _state.WaveScores.Count)
            {
                _state.WaveScores[waveIndex] += gained; // 波次得分 = 该波次各回合结算得分之和
            }
            _state.BaseScore = 0;
            _state.ScoreMultiplier = 1;
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "score"); // 得分变化（前端刷新）
        }

        /// <summary>
        /// 敌方击杀扣分（2026-08-20 策划口述——第 3/4 关可用，按关开关 FloorConfig.scoreDeductEnabled）：
        /// 我方棋子被**敌方棋子**击败 → 本关总得分 - 该棋子价值（clamp 0——总得分不下负）。
        /// **接口**：独立封装，供后续任何"被敌方击败扣分"调用点复用（HandleDeath 已挂）。
        /// </summary>
        public void DeductScoreForPlayerLose(PieceInstance lostPiece)
        {
            if (lostPiece == null || lostPiece.side != Side.Player) return;
            int value = PieceValue.SumValue(lostPiece.GetProgram(_state)); // 被击败棋子价值（生效程序推导）
            _state.PlayerScore = Mathf.Max(0, _state.PlayerScore - value); // 扣本关总得分（clamp 0）
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "score"); // 得分变化（前端刷新）
        }

        private void OnKillTriggers(PieceInstance victim, PieceInstance killer)
        {
            // 特殊能力（OnKill + ExtraAction）：【击杀者】获得免费执行资格（方案 B——不立即执行，
            // 玩家点击该棋子执行时免费；同一棋子只登记一次；有效期待策划拍板——当前保留到使用为止）
            if (killer != null)
            {
                foreach (var ability in killer.GetAllAbilities())
                {
                    if (ability.type == SpecialAbilityType.Trigger && ability.triggerPoint == TriggerPoint.OnKill
                        && ability.triggerEffect == TriggerEffect.ExtraAction)
                    {
                        if (_state.FreeExecutes.Add(killer.Id))
                        {
                            EventCenter.Instance.EventTrigger(GameEvent.ExtraActionGranted, killer.Id);
                            EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, killer.Id);
                        }
                    }
                }
            }
            // 触发型遗物（OnKill）：击杀者回血（经 Resolver 统一入口）
            if (killer != null)
            {
                foreach (var relic in _state.Relics)
                {
                    foreach (var ability in relic.abilities)
                    {
                        if (ability.type == SpecialAbilityType.Trigger && ability.triggerPoint == TriggerPoint.OnKill
                            && ability.triggerEffect == TriggerEffect.HealDurability)
                        {
                            ModifyDurability(killer, ability.amount);
                        }
                    }
                }
            }
            // FloorRules.OnKill（层差异钩子——由 BattleFlow 在守卫内调用，此处注释占位）
        }

        // ========== 事件/编辑/加牌效果（经 Resolver 落账——唯一写入口）==========

        /// <summary>编辑程序落账（实时编辑/事件 EditProgram——改种类级表）。</summary>
        public void ApplyProgramEdit(int defId, List<Template> program)
        {
            _state.CurrentPrograms[defId] = program;
            EventCenter.Instance.EventTrigger(GameEvent.ProgramEdited, defId);
        }

        /// <summary>玩家手牌加牌（事件 AddPiece 效果；2026-08-20 牌结构——棋子牌入牌）。</summary>
        public void AddToHand(int defId)
        {
            _state.Hand.Add(Card.Piece(defId));
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>玩家手牌加牌（带属性——属性玩法复制牌"属性相同"；麻将牌非棋子不带属性请用 AddMahjongToHand）。</summary>
        public void AddToHand(Card card)
        {
            _state.Hand.Add(card);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>
        /// 牌组构筑落账（DeckBuild 事件——整组替换手牌，含牌数/总价值校验）。
        /// 限制来自当前事件定义（EventDefinition.deckSizeLimit/totalValueLimit；0 = 不限制；
        /// allowDuplicate 可复数 / promoteLimitByInitial 升变≤初始——2026-08-15 策划新案，事件级开关，默认 false = 旧行为）。
        /// 校验失败返回 false 且不改状态（UI 提示后保持面板编辑态）。
        /// </summary>
        public bool BuildDeck(List<int> defIds)
        {
            // ⚠️ 2026-08-12：空牌组校验（下限型——原校验全是上限型，空列表通过 → 手牌清空 → 无棋无牌开局即败）
            if (defIds == null || defIds.Count == 0)
            {
                return false; // 至少 1 张——规则层兜底，不依赖 UI
            }

            // 当前事件限制（CurrentEventId 查 EventDefinition；查不到 = 拒绝——构筑必须处于事件上下文，防超限绕过）
            var ev = string.IsNullOrEmpty(_state.CurrentEventId)
                ? null
                : ConfigTable.FindByName<EventDefinition>(_state.CurrentEventId);
            if (ev == null)
            {
                Core.Assert.Fail($"BuildDeck: 无活动事件（CurrentEventId='{_state.CurrentEventId}'）——构筑拒绝（2026-08-11 加固）");
                return false;
            }

            // 去重校验：事件开关 allowDuplicate=true 允许复数编入（新案）；默认去重（旧行为——同种棋子一张）
            var effective = new List<int>();
            var seen = new HashSet<int>();
            foreach (var id in defIds)
            {
                if (!ev.allowDuplicate && !seen.Add(id))
                {
                    return false;
                }
                effective.Add(id);
            }

            int sizeLimit = ev.deckSizeLimit;
            // ⚠️ 死代码（测试占位机制——2026-08-20 用户拍板废除）：构筑"总价值 ≤ 上限"限制。
            // 原本只是测试阶段占位（events.json deck_standard 的 totalValueLimit:30）；正式规则 = 满 12 张 +
            // 可重复 + 升变≤初始（无价值上限）。保留 valueLimit/totalValue 变量与累计仅为可读性（不再校验）——
            // 有关接口（EventDefinition.totalValueLimit / 前端总价值 tag / BuildDeck 价值校验）全部废除。
            int valueLimit = ev.totalValueLimit; // 死代码：不再生效（原占位限制值——保留字段兼容旧配置）

            int totalValue = 0; // 死代码：原价值累计（保留——不再用于校验）
            int initialCount = 0;
            int promotedCount = 0;
            foreach (var id in effective)
            {
                var def = ConfigTable.Find<PieceDef>(id);
                if (def == null)
                {
                    return false; // 牌组含未知棋子——配置缺失当场拒绝
                }
                // ⚠️ 死代码：totalValue 累计不再参与校验（价值上限已废除——见上）；保留计算仅供注释参考
                totalValue += _state.GetEffectiveValue(id);
                if (ev.promoteLimitByInitial)
                {
                    var t = _state.GetEffectiveType(id); // 升变≤初始：按当前价值档位计数
                    if (t == PieceType.Initial) initialCount++;
                    else if (t == PieceType.Promoted) promotedCount++;
                }
            }
            // ⚠️ 2026-08-19：必须**选满**（策划确认"构筑事件中必须构筑满 12 个棋子"）——原为上限型（Count ≤ sizeLimit 通过）
            if (sizeLimit > 0 && effective.Count != sizeLimit) return false;
            // 死代码（2026-08-20 废除）：价值上限校验——不再执行（原：if (valueLimit > 0 && totalValue > valueLimit) return false;）
            if (ev.promoteLimitByInitial && promotedCount > initialCount) return false; // 升变数量 ≤ 初始数量

            // 通过校验：整组替换手牌（落账纪律——唯一写入口）
            // ⚠️ 2026-08-13：按入参顺序写入（原 AddRange(seen) 迭代 HashSet——顺序不确定；
            // 去重校验仍由 seen 完成，顺序按玩家选择顺序——存档往返一致）
            // ⚠️ 2026-08-20 牌结构：构筑只选棋子牌 → 转 Card（无属性）
            _state.Hand.Clear();
            _state.Hand.AddRange(effective.ConvertAll(id => Card.Piece(id))); // 顺序 = 入参顺序（可复数模式下含重复）
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
            return true;
        }

        /// <summary>
        /// 抽牌堆初始化（2026-08-19 策划确认：第一回合开始前调用——手牌中【部署/升变】种类棋子转入抽牌堆；
        /// 初始种类已由 Placement 阶段全部部署完——校验兜底）。落账纪律：唯一写入口。
        /// </summary>
        public void SetupDrawPile()
        {
            // ⚠️ 2026-08-20 麻将玩法：战斗开始 18 张麻将牌放进抽牌堆（与棋子牌混合——第一回合抽 4/摸切可抽到）
            if (_state.IsStyleActive(Mahjong.StyleId))
            {
                foreach (var tile in Mahjong.Tiles())
                {
                    _state.DrawPile.Add(tile);
                }
            }
            for (int i = _state.Hand.Count - 1; i >= 0; i--)
            {
                var card = _state.Hand[i];
                // 非初始棋子牌转抽牌堆（麻将牌 IsMahjong 亦转——本应直接进抽牌堆，此处防御）；
                // 初始棋子牌已由 Placement 阶段全部署（校验兜底）
                if (!card.IsPiece || _state.GetEffectiveType(card.defId) != PieceType.Initial)
                {
                    _state.DrawPile.Add(card);
                    _state.Hand.RemoveAt(i);
                }
            }
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>抽 1 张（2026-08-20：从抽牌堆**随机**抽一张 → 手牌——策划确认；抽牌堆空 = 无操作——调用方先校验；RandomManager 种子相关可复现）。</summary>
        public void DrawCard()
        {
            if (_state.DrawPile == null || _state.DrawPile.Count == 0)
            {
                return;
            }
            int idx = RandomManager.Instance.Range(0, _state.DrawPile.Count); // 随机抽
            var card = _state.DrawPile[idx];
            _state.DrawPile.RemoveAt(idx);
            _state.Hand.Add(card);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>敌方波次池增强（加牌落点：敌方无手牌——增强未来波次阵容）。</summary>
        public void AddToEnemyWavePool(int defId)
        {
            _state.AddToEnemyWavePool(defId);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, null);
        }

        /// <summary>获得遗物（事件 GrantRelic 效果——整局持续、可叠加）。</summary>
        public void AddRelic(string relicName)
        {
            var relic = ConfigTable.FindByName<RelicDef>(relicName);
            if (relic == null)
            {
                Core.Assert.Fail($"GrantRelic: 找不到遗物资产 {relicName}");
                return;
            }
            _state.Relics.Add(relic);
            EventCenter.Instance.EventTrigger(GameEvent.RelicObtained, relic);
        }

        /// <summary>给予临时特殊能力（事件 GrantAbility——随战斗结束销毁）。护盾能力同步增加剩余次数。</summary>
        public void GrantTempAbility(int pieceId, int abilityId)
        {
            var piece = _state.GetPiece(pieceId);
            var ability = ConfigTable.Find<SpecialAbilityDef>(abilityId);
            if (piece != null && ability != null)
            {
                piece.tempAbilities.Add(ability);
                if (ability.type == SpecialAbilityType.Trigger && ability.triggerPoint == TriggerPoint.OnDamaged
                    && ability.triggerEffect == TriggerEffect.ShieldBlock)
                {
                    piece.shieldCount += ability.amount; // 获得护盾能力 = 获得对应抵挡次数
                }
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, pieceId); // buff 变化 → UI 刷新
            }
        }

        /// <summary>按 defId 对首名匹配棋子修改承伤（事件 ModifyDurability 简化版——目标选择完善后替换）。</summary>
        public void ModifyTargetDurability(int defId, int amount)
        {
            foreach (var piece in _state.Pieces.Values)
            {
                if (piece.DefId == defId)
                {
                    ModifyDurability(piece, amount);
                    return;
                }
            }
        }

        // ========== 待执行（已并入免费资格机制——2026-08-11 方案 B）==========
        // 额外行动 = FreeExecutes 资格（GameState）——玩家点击该棋子执行时免费；
        // 旧的 RequestExtraExecute/_pendingExtraExecutes 立即执行机制已删除；
        // EnqueuePending/_pendingActions 从未被使用（死代码）一并删除。
        // 若未来需要"延迟落账"（真实需求出现时）再按需加回（git 历史可恢复）。

        // ========== 日志 / 回放 ==========

        private void LogAction(ConcreteAction action)
        {
            _state.ReplayLog.Add(action); // 回放记录（数据）
            Debug.Log($"[Resolver] 落账: {action.GetType().Name}"); // 落账日志（现场还原）
        }
    }

    // ========== 事件数据（UI 表现依据）==========

    public class MoveInfo
    {
        public int PieceId;
        public Vector2Int From;
        public Vector2Int To;
    }

    public class DamageInfo
    {
        public int AttackerId;
        public int TargetId;            // 目标棋子 id（-1=空放）——死亡后从 Pieces 移除，UI 需在死亡前记录
        public Vector2Int TargetCell;
        public int Damage;
        public bool TargetDied;
        public bool FriendlyFire;
    }

    public class DeployInfo
    {
        public int PieceId;
        public int DefId;
        public Side Side;
        public Vector2Int Cell;
    }

    public class PromoteInfo
    {
        public int PieceId;
        public int NewDefId;
    }

    public class DeathInfo
    {
        public int PieceId;
        public Side Side;
        public int KillerId = -1; // -1=无击杀者（非攻击击杀）
    }

    public class ExtraExecutePending
    {
        public List<int> PieceIds;
    }
}
