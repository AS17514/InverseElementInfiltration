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
                if (_state.IsStyleActive(StyleRegistry.Element) && piece.element != Element.None && target.element != Element.None)
                {
                    if (ElementRules.IsCountering(piece.element, target.element))
                    {
                        // 相克：直接击败（无视护盾/抗性——伤害打穿护盾+承伤）+ 基础得分 + 棋盘上同属性棋子数量
                        int bypass = target.durability + target.shieldCount + 1;
                        TraceBattle($"攻击判定: 攻击者 def={piece.DefId} 元素={piece.element} → 目标 def={target.DefId} 元素={target.element} 相克(bypass={bypass})");
                        died = ModifyDurability(target, -bypass, piece);
                        AddBaseScore(CountSameElementOnBoard(piece.element)); // 2026-08-20 计分统一入口（相克额外得分）
                        elementHit = true;
                    }
                    else if (ElementRules.IsGenerating(piece.element, target.element))
                    {
                        // 相生：不造成任何伤害 + 获得目标复制牌入手牌（属性相同）
                        TraceBattle($"攻击判定: 攻击者 def={piece.DefId} 元素={piece.element} → 目标 def={target.DefId} 元素={target.element} 相生(复制牌)");
                        HandAddCard(Card.Piece(target.DefId, target.element)); // 统一牌区入口
                        elementHit = true;
                        // died 保持 false（无伤害）
                    }
                    else
                    {
                        TraceBattle($"攻击判定: 攻击者 def={piece.DefId} 元素={piece.element} → 目标 def={target.DefId} 元素={target.element} 普通伤害={damage}");
                        died = ModifyDurability(target, -damage, piece); // 无关：正常伤害
                    }
                }
                else
                {
                    TraceBattle($"攻击判定: 攻击者 def={piece.DefId} 目标格=({action.targetCell.x},{action.targetCell.y}) 目标=无（空放/墙体）");
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
                AttackMode = action.template.mode, // 2026-08-23 攻击音分发（前端契约）
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
                    TraceBattle($"AOE命中: 攻击者 def={attacker.DefId} → 目标 def={victim.DefId} side={victim.side} @({cell.x},{cell.y}) 伤害={damage}");
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
                        AttackMode = action.template.mode, // 2026-08-23 攻击音分发（前端契约——AOE 命中）
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
                    AttackMode = action.template.mode, // 2026-08-23 攻击音分发（前端契约——AOE 空放）
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
            int consumedCardIndex = action.side == Side.Player
                ? FindHandCardIndex(action.pieceDefId, action.cardInstanceId)
                : -1;
            if (action.side == Side.Player && consumedCardIndex < 0)
            {
                Debug.LogWarning($"[Resolver] 部署拒绝：手牌无匹配牌（defId={action.pieceDefId} id={action.cardInstanceId}）");
                return; // 精确消费失败/无牌——拒绝部署（不隐式乱打）
            }
            Card consumedCard = consumedCardIndex >= 0 ? _state.Hand[consumedCardIndex] : default;
            var piece = new PieceInstance(def, action.side, action.cell)
            {
                Id = _state.AllocatePieceId(),
                waveIndex = action.waveIndex, // 波次标（每波得分累计用）
            };
            // ⚠️ 2026-08-20「属性」玩法：创建时分配属性（一方应含双方棋子——部署/波次都经此）
            // 复制牌（Card 带属性）优先用实际消耗牌属性；否则激活玩法时随机（金木水火土）
            if (_state.IsStyleActive(StyleRegistry.Element))
            {
                piece.element = consumedCard.element;
                if (piece.element == Element.None)
                {
                    piece.element = RandomElement();
                }
            }
            if (consumedCardIndex >= 0)
            {
                HandRemovePieceAt(consumedCardIndex); // 玩家部署：移除实际消耗的同一张牌（统一牌区入口）
            }
            _state.Pieces[action.cell] = piece;
            _state.PiecesById[piece.Id] = piece;
            // ⚠️ 2026-08-24 骰子玩法：部署"价值 = 骰子点数"的棋子 → 倍率 +1（正常耗 AP 部署，额外奖励；不消耗点数）
            if (action.side == Side.Player && _state.IsStyleActive(StyleRegistry.Dice)
                && _state.DiceValue > 0 && PieceValue.SumValue(piece.GetProgram(_state)) == _state.DiceValue)
            {
                AddMultiplier(1);
            }
            EventCenter.Instance.EventTrigger(GameEvent.PieceDeployed, new DeployInfo { PieceId = piece.Id, DefId = action.pieceDefId, Side = action.side, Cell = action.cell, CardInstanceId = action.cardInstanceId });
        }

        /// <summary>
        /// 找到一张实际消耗的手牌（2026-08-21：优先按 cardInstanceId 精确匹配（前端显式指定"打哪张"）；
        /// id 无效/缺省（0——旧请求）→ 回退：优先带属性牌，允许同 defId 重复。
        /// 返回索引；未找到 = -1。
        /// </summary>
        private int FindHandCardIndex(int defId, int cardInstanceId)
        {
            // ① 显式 id：精确定位（校验 defId 匹配——防传错组合）
            if (cardInstanceId > 0)
            {
                for (int i = 0; i < _state.Hand.Count; i++)
                {
                    var c = _state.Hand[i];
                    if (c.instanceId == cardInstanceId)
                    {
                        if (c.IsPiece && c.defId == defId) return i;
                        Debug.LogWarning($"[Resolver] 精确消费失败：实例 {cardInstanceId} 与 defId {defId} 不匹配——拒绝（实例实际 defId={c.defId}）");
                        return -1;
                    }
                }
                Debug.LogWarning($"[Resolver] 精确消费失败：实例 {cardInstanceId} 不在手牌——拒绝");
                return -1;
            }
            // ② 隐式回退（旧请求）：优先带属性牌
            int fallback = -1;
            for (int i = 0; i < _state.Hand.Count; i++)
            {
                var card = _state.Hand[i];
                if (!card.IsPiece || card.defId != defId) continue;
                if (fallback < 0) fallback = i;
                if (card.element != Element.None) return i;
            }
            return fallback;
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
            int consumedCardIndex = piece.side == Side.Player ? FindHandCardIndex(action.newDefId, action.cardInstanceId) : -1;
            if (piece.side == Side.Player && consumedCardIndex < 0)
            {
                Debug.LogWarning($"[Resolver] 升变拒绝：手牌无匹配升变牌（defId={action.newDefId} id={action.cardInstanceId}）");
                return; // 精确消费失败/无牌——拒绝升变（不白升变）
            }
            var oldDefId = piece.DefId;      // 升变前 def（相生复制牌用——被升变棋子）
            var oldElement = piece.element;  // 升变前属性（相克/相生判定用——旧棋子 vs 新棋子）
            piece.def = newDef;
            piece.ApplyDefProperties(); // 承伤+护盾按新身体重算（2026-08-12：护盾此前漏算——升变丢新身体护盾；统一初始化路径）
            // ⚠️ 2026-08-24 策划新语义（补充）：被升变的棋子（原牌）**立刻**进入弃牌区（升变瞬间记录，非死亡时两张）——
            // 死亡时弃牌区只记升变牌（当前形态）——见 HandleDeath；**仅玩家侧**（敌方无"牌"概念——敌方升变不写玩家弃牌区）
            if (piece.side == Side.Player)
            {
                _state.Discard.RecordPieceDeath(Card.Piece(oldDefId, oldElement));
            }
            // ⚠️ 2026-08-20「属性」玩法：升变 = 新身体 → 属性重随机（激活玩法时）；
            // 相克/相生判定（升变棋子 vs 被升变棋子属性）：
            //   相克 → 倍率 +1（当回合——结算后复位 1）；相生 → 被升变棋子的复制牌入手牌（属性 = 旧属性）
            if (_state.IsStyleActive(StyleRegistry.Element))
            {
                piece.element = RandomElement();
                if (ElementRules.IsCountering(piece.element, oldElement))
                {
                    AddMultiplier(1); // 升变相克：倍率 +1（2026-08-20 计分统一入口）
                }
                else if (ElementRules.IsGenerating(piece.element, oldElement))
                {
                    HandAddCard(Card.Piece(oldDefId, oldElement)); // 复制牌：属性 = 旧属性（统一牌区入口）
                }
            }
            if (consumedCardIndex >= 0)
            {
                HandRemovePieceAt(consumedCardIndex); // 升变牌打出：移除实际消耗的同一张牌（统一牌区入口）
            }
            // ⚠️ 2026-08-24 牌去向记录：场上原牌被升变替换 → 进升变替换池（仅玩家侧——敌方无"牌"概念；
            // 升变后的新牌（升变牌）死亡仍由 HandleDeath 记入墓地——记死亡时 defId = 升变后形态）
            if (piece.side == Side.Player)
            {
                _state.RecordPromotedReplaced(Card.Piece(oldDefId, oldElement));
            }
            // ⚠️ 2026-08-22 能力 PromoteCopyDeployable：升变"部署"棋子 → 该棋子一张复制加入手牌（通用版——非属性玩法）
            if (_state.HasRelicEffect(RelicEffectType.PromoteCopyDeployable)
                && _state.GetEffectiveType(oldDefId) == PieceType.Deployable)
            {
                HandAddCard(Card.Piece(oldDefId));
            }
            EventCenter.Instance.EventTrigger(GameEvent.PiecePromoted, new PromoteInfo { PieceId = piece.Id, NewDefId = action.newDefId, CardInstanceId = action.cardInstanceId });
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
                    TraceBattle($"承伤: id={piece.Id} def={piece.DefId} 护盾吸收={absorbed} 剩余护盾={piece.shieldCount}");
                }
                delta += absorbed;
                if (delta >= 0)
                {
                    return false; // 伤害被完全抵挡——承伤不变
                }
            }
            piece.durability += delta;
            TraceBattle($"承伤: id={piece.Id} def={piece.DefId} side={piece.side} delta={delta} 剩余={piece.durability} killer={(killer != null ? killer.Id : -1)}");
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
            // 2026-08-23 死亡回放记录（补"死亡黑盒"缺口——killer=-1 = 非攻击击杀/未知来源；离线推演直读死因）
            _state.ReplayLog.Add(new DeathAction(victim.Id, victim.DefId, victim.side, killer != null ? killer.Id : -1, victim.position)
            {
                side = victim.side,
                defId = victim.DefId,
                turn = _state.TurnCount,
            });
            Debug.Log($"[Resolver] 死亡: id={victim.Id} def={victim.DefId} side={victim.side} @({victim.position.x},{victim.position.y}) killer={(killer != null ? killer.Id : -1)}");
            _state.Pieces.Remove(victim.position);
            _state.PiecesById.Remove(victim.Id);

            // 击杀积分（价值分——2026-08-15 改走推导：价值 = 生效程序槽位总和，编辑后随程序变化）
            // + 墓地（仅玩家棋子；敌方无手牌概念）
            // ⚠️ 2026-08-19 计分规则：击败敌方棋子 → **基础得分** + 该棋子价值（固定得分规则）；
            // 总得分/波次得分由回合结算（SettleScore）统一入账（原击杀直加 PlayerScore/WaveScores 移除）
            int killValue = PieceValue.SumValue(victim.GetProgram(_state));
            if (victim.side == Side.Player)
            {
                // ⚠️ 2026-08-24 围棋棋子：不进墓地/弃牌区（"棋子牌"未消耗、停留手上——B6 定稿）；价值 0 无分
                if (!victim.IsGo)
                {
                    _state.Graveyard.Add(victim.DefId);
                    // 弃牌区·棋子死亡（Card 化——双写 A：Graveyard 保留 + DiscardZone 权威）
                    // ⚠️ 2026-08-24 策划新语义：死亡时**只记升变牌（当前形态）**——原牌已在升变瞬间进弃牌区（ResolvePromote）
                    _state.Discard.RecordPieceDeath(Card.Piece(victim.DefId, victim.element));
                    // ⚠️ EnemyScore：**无策划依据的遗留实现**（2026-08-19 确认保留——仅结算面板显示"敌方得分"，不参与任何判定；
                    // 玩家计分规则（回合计分）只有玩家侧——见 计分规则_策划口述_20260819.md）
                    _state.EnemyScore += killValue;
                    // ⚠️ 2026-08-20 敌方击杀扣分（策划口述——第 3/4 关可用，按关开关）：我方棋子被**敌方棋子**击败 → 本关总得分 - 该棋子价值
                    if (killer != null && killer.side == Side.Enemy && (_state.CurrentFloorConfig?.scoreDeductEnabled ?? false))
                    {
                        DeductScoreForPlayerLose(victim);
                    }
                }
            }
            else
            {
                AddBaseScore(killValue); // 基础得分积累（回合结束按 基础分×倍率 结算——2026-08-20 计分统一入口）
                // ⚠️ 2026-08-20 麻将玩法：敌方棋子被击败 → 该牌价值数字填入牌山（刻/顺判定——番数）
                if (_state.IsStyleActive(Mahjong.StyleId))
                {
                    PushMahjongScore(killValue);
                }
            }

            // OnKill 触发点（层差异 + 遗物 + 特殊能力——动作进待执行队列）
            OnKillTriggers(victim, killer);

            // 升变预告清理（2026-08-23）：死亡 = 预告随棋子结束生命周期（残留预告逢倒计时也会被移除，
            // 但此处立即清 + 发 BuffsChanged——"死亡即结束"语义 + buff 区即时不残留）
            if (_state.PromoteAnnouncements.RemoveAll(a => a.pieceId == victim.Id) > 0)
            {
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, victim.Id);
            }

            // 免费执行资格 + 行动经济已行动标记清理（2026-08-23）：资格/buff 属于棋子实例——死亡即随棋子失效
            // （原残留：资格集合不清 → 战斗结束 ResetForBattle 也不清（_nextPieceId 重置后 Id 可能复用）→ 资格串到新棋子）
            bool removedAny = _state.FreeExecutes.Remove(victim.Id) || _state.ActionEconomyActed.Remove(victim.Id);
            if (removedAny)
            {
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, victim.Id);
            }

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
            // 2026-08-24 牌去向记录：先取实际那张牌（含实例 id）——墙体记录 id（破坏时精确转移使用池→死亡池）+ 记使用池
            var actual = TakeMahjongFromHand(mahjongCard.value); // 手牌移除该麻将牌（同点数多张——只移除一张）
            int instanceId = actual.HasValue ? actual.Value.instanceId : 0;
            _state.MahjongWalls[cell] = new ObstacleData(mahjongCard.value, instanceId);
            _state.MahjongWalls[second] = new ObstacleData(mahjongCard.value, instanceId);
            if (actual.HasValue) _state.RecordMahjongUsed(actual.Value); // 使用池：打出墙体 = 使用（在场上也算使用；被破坏才转死亡池）
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
            _state.MoveMahjongUsedToDead(wall.instanceId, wall.value); // 2026-08-24：使用池→死亡池（精确按 instanceId；0/找不到兜底按点数——同点数等价）
            var deadCard = Card.Mahjong(wall.value); // 2026-08-24 弃牌区·麻将死亡（墙体破坏后才进弃牌区——记录实际那张牌含实例 id）
            deadCard.instanceId = wall.instanceId;
            _state.Discard.RecordMahjongDeath(deadCard);
            PushMahjongScore(wall.value);       // 破坏 → 填牌山点数
            AddBaseScore(1);                     // 破坏 → 基础得分 +1（2026-08-20 计分统一入口——顺带补发计分事件）
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "mahjong-wall");
        }

        /// <summary>
        /// 麻将：摸切（手牌行动）——手牌麻将牌填入牌山 + 抽一张牌。
        /// 抽一张 = 从抽牌堆（DrawCard）；手牌移除该麻将。
        /// 2026-08-24：实际取走的那张牌（含实例 id）记录进麻将使用池。
        /// </summary>
        public void MochiCut(Card mahjongCard)
        {
            if (!mahjongCard.IsMahjong) return;
            var actual = TakeMahjongFromHand(mahjongCard.value);
            if (actual.HasValue) _state.RecordMahjongUsed(actual.Value); // 使用池：摸切 = 使用（手牌消耗）
            _state.Discard.RecordMahjongDeath(actual ?? mahjongCard); // 2026-08-24 弃牌区·麻将死亡（摸切"使用即入"——含实例 id）
            PushMahjongScore(mahjongCard.value);
            DrawCard();
        }

        /// <summary>
        /// 手牌取走一张指定点数的麻将牌（Card 值语义——同点数多张只去一张；统一牌区入口）。
        /// 返回实际取走的那张（含实例 id——麻将池记录用）；手牌无该点数 = null。
        /// </summary>
        private Card? TakeMahjongFromHand(int value)
        {
            int idx = _state.Hand.FindIndex(c => c.IsMahjong && c.value == value);
            if (idx < 0) return null;
            var card = _state.Hand[idx];
            HandRemoveMahjong(value);
            return card;
        }

        /// <summary>
        /// 麻将：和牌（手牌行动——1 AP）——条件（BattleFlow 校验）：手牌存在雀头（任意两牌价值相同——不限麻将牌）且番数 &gt; 0。
        /// 效果：本回合倍率 + 番数、番数清零。
        /// 返回是否成功（触发了和牌）。
        /// </summary>
        public bool Hu(int fanToUse)
        {
            if (fanToUse <= 0) return false;
            AddMultiplier(fanToUse); // 倍率 + 番数（当回合——结算后复位 1；2026-08-20 计分统一入口）
            _state.FanCount = 0;                // 番数清零
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "mahjong-hu");
            return true;
        }

        // ========== 玩法·骰子（2026-08-24 设计定稿——仅玩家侧）==========

        /// <summary>投掷（执行类行动 1 AP——BattleFlow 校验）：随机 1~6（6 面骰）→ DiceValue + 基础分 + 点数。</summary>
        public void RollDice()
        {
            _state.DiceValue = RandomManager.Instance.Range(1, 7); // RandomManager——种子相关可复现
            AddBaseScore(_state.DiceValue); // 基础得分 + 骰子点数
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice");
        }

        /// <summary>启动"点数直线移动"（消耗点数、不耗 AP）：点数清零 + 全场挂 buff（下次点棋子执行时重定向为点数步直线移动；其他行动不取消；不跨回合）。</summary>
        public bool StartDiceMove()
        {
            if (_state.DiceValue <= 0) return false;
            _state.DiceMoveSteps = _state.DiceValue; // 步数 = 启动时点数
            _state.DiceValue = 0;                    // 点数消耗（"消耗骰子的点数"）
            _state.DiceMovePending = true;           // 全场我方 buff
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice");
            return true;
        }

        /// <summary>骰子移动执行完成（清 buff——BattleFlow 落账 MoveAction 后调用）。</summary>
        public void FinishDiceMove()
        {
            _state.DiceMovePending = false;
            _state.DiceMoveSteps = 0;
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "dice");
        }

        // ========== 玩法·围棋（2026-08-24 设计定稿——仅玩家侧）==========

        /// <summary>部署"棋子牌"（不耗 AP、每回合 1 次、任意空格——BattleFlow 校验）：蓝红 side 切换（首次蓝）+ 围杀检查。</summary>
        public bool DeployGoPiece(Vector2Int cell)
        {
            if (!_state.IsStyleActive(StyleRegistry.Go)) return false;
            if (_state.GoDeployCount >= 1) return false; // 每回合限 1 次（BattleFlow 已校验——防御）
            if (_state.Pieces.ContainsKey(cell)) return false; // 空格（任意格——非占用即可）
            // 颜色切换：首次蓝（Player）；之后每次部署切换（上次红→本次蓝、上次蓝→本次红）
            Side side = !_state.GoEverDeployed ? Side.Player : (_state.GoLastColor == Side.Player ? Side.Enemy : Side.Player);
            _state.GoEverDeployed = true;
            _state.GoLastColor = side;
            var piece = new PieceInstance(GoPiece.GetDef(), side, cell)
            {
                Id = _state.AllocatePieceId(),
                IsGo = true,
            };
            _state.Pieces[cell] = piece;
            _state.PiecesById[piece.Id] = piece;
            _state.GoDeployCount++;
            EventCenter.Instance.EventTrigger(GameEvent.PieceDeployed, new DeployInfo { PieceId = piece.Id, DefId = GoPiece.DefId, Side = side, Cell = cell });
            CheckGoCapture(cell); // 落子瞬间围杀检查（2026-08-24 策划新语义：只有本次落子补全的包围才生效）
            return true;
        }

        /// <summary>
        /// 围杀检查（2026-08-24 策划新语义——**落子瞬间一次性判定**）：只有本次落子**正好组成包围**（被围棋子四邻含本次落子格）才生效；
        /// 被围住（四邻全被对方色围棋占据、边界算墙）的棋子死——一次性计算该区域；**之后该区域可正常有相反颜色的别的棋子生存**（无持续判定）。
        /// 包围者只能是围棋棋子（真实棋子不参与）；每提一子 → 倍率 +1（"自己刷分提升倍率"）。
        /// </summary>
        private void CheckGoCapture(Vector2Int placedCell)
        {
            if (!_state.IsStyleActive(StyleRegistry.Go)) return;
            var victims = new List<PieceInstance>();
            foreach (var piece in _state.Pieces.Values)
            {
                if (piece == null) continue;
                // 只有本次落子补全的包围才判定（被围棋子四邻必须包含本次落子格）；无连锁（围棋不可移动）
                if (IsAdjacentTo(piece, placedCell) && IsSurroundedByGo(piece)) victims.Add(piece);
            }
            foreach (var v in victims)
            {
                if (!_state.PiecesById.ContainsKey(v.Id)) continue; // 防御
                AddMultiplier(1); // 围杀 → 倍率 +1
                HandleDeath(v, null); // 提子（killer=null——非攻击击杀）
            }
        }

        private static bool IsAdjacentTo(PieceInstance piece, Vector2Int cell)
        {
            var pos = piece.position;
            return (pos + Vector2Int.up) == cell || (pos + Vector2Int.down) == cell
                || (pos + Vector2Int.left) == cell || (pos + Vector2Int.right) == cell;
        }

        /// <summary>某棋子四邻是否全被"对方色围棋棋子"占据（出界=边界算墙=无气贡献；空格/非围棋棋子/己方围棋=有气）。</summary>
        private bool IsSurroundedByGo(PieceInstance piece)
        {
            Side surroundColor = piece.side == Side.Player ? Side.Enemy : Side.Player; // 包围色 = 对方色围棋（真实棋子不参与包围）
            foreach (var dir in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
            {
                var cell = piece.position + dir;
                if (!IsInsideBoard(cell)) continue; // 边界算墙（无气贡献）
                var other = _state.GetPieceAt(cell);
                if (other == null) return false;                          // 空格 = 有气
                if (!other.IsGo || other.side != surroundColor) return false; // 非包围色围棋（真实棋子/己方围棋）= 有气
            }
            return true; // 四邻全被包围色围棋占据（或墙）→ 无气
        }

        private static bool IsInsideBoard(Vector2Int cell) => cell.x >= 0 && cell.x < 8 && cell.y >= 0 && cell.y < 8;

        /// <summary>玩法激活落账（2026-08-24：改变规则事件/玩法选择机制落地后调用——ActiveStyles 唯一写入口；2026-08-24 由 SelectRule 调用）。</summary>
        public void SetStyleActive(string style)
        {
            if (string.IsNullOrEmpty(style)) return;
            _state.ActiveStyles.Add(style);
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "style");
        }

        // ========== 玩法事件·二选一（2026-08-24 策划定案——决策记录_玩法选择机制_二选一）==========

        /// <summary>
        /// 玩法事件候选抽取（事件打开时调用——isRulePick）：从未激活玩法随机抽 2（无放回）——
        /// 候选池 = 全部玩法 − ActiveStyles（已选玩法后续不再出现）；**落选玩法保留**（后续仍可能再出现——无永久排除记录，由池推导）；
        /// 池 <2 时按实际数量（当前 5 玩法 + 第 2-4 关各 1 次事件不会触发——防御）；发 RuleCandidatesDrawn。
        /// RandomManager 种子相关可复现（读档续玩候选一致）。
        /// </summary>
        public void DrawRuleCandidates()
        {
            var available = new List<string>();
            foreach (var style in StyleRegistry.All)
            {
                if (!_state.ActiveStyles.Contains(style)) available.Add(style);
            }
            var picked = new List<string>();
            var copy = new List<string>(available);
            while (picked.Count < 2 && copy.Count > 0)
            {
                int idx = RandomManager.Instance.Range(0, copy.Count);
                picked.Add(copy[idx]);
                copy.RemoveAt(idx);
            }
            _state.RuleCandidates = picked;
            EventCenter.Instance.EventTrigger(GameEvent.RuleCandidatesDrawn, _state.RuleCandidates);
        }

        /// <summary>玩法事件选择落账（2026-08-24：选候选 index → 激活（SetStyleActive——唯一写入口）→ 清候选 → 事件完成推进）。</summary>
        public void SelectRule(int choiceIndex)
        {
            if (choiceIndex < 0 || choiceIndex >= _state.RuleCandidates.Count) return;
            var style = _state.RuleCandidates[choiceIndex];
            if (string.IsNullOrEmpty(style)) return;
            SetStyleActive(style); // 激活（已选玩法后续不再出现——池推导自然排除）
            _state.RuleCandidates.Clear();
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted, _state.CurrentEventId); // 事件完成（推进流程）
        }

        // ========== 玩法·代币（2026-08-24 设计定稿——仅玩家侧；不跨战斗）==========

        /// <summary>弃牌区统一查询视图（棋子死亡 + 麻将死亡——代币购买选择/前端展示用）。</summary>
        public List<Card> DiscardView()
        {
            var list = new List<Card>();
            list.AddRange(_state.Discard.PieceDeaths);
            list.AddRange(_state.Discard.MahjongDeaths);
            return list;
        }

        /// <summary>购买（2026-08-24）：选弃牌区一张牌 → 消耗该牌价值数代币（棋子=推导价值；麻将=点数）→ 复制入手牌 + 基础分+消耗数；每回合不限次。</summary>
        public bool BuyFromDiscard(int discardIndex)
        {
            if (!_state.IsStyleActive(StyleRegistry.Token)) return false;
            var view = DiscardView();
            if (discardIndex < 0 || discardIndex >= view.Count) return false;
            var card = view[discardIndex];
            int cost = CardValueForToken(card);
            if (cost <= 0 || _state.TokenCount < cost) return false; // 代币不足（含 0 价值牌不可买）
            _state.TokenCount -= cost;
            if (card.IsPiece)
            {
                HandAddCard(Card.Piece(card.defId, card.element)); // 复制（新实例 id 统一分配）
            }
            else if (card.IsMahjong)
            {
                HandAddCard(Card.Mahjong(card.value));
            }
            else
            {
                return false;
            }
            AddBaseScore(cost); // 基础得分 + 消耗代币数
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "token");
            return true;
        }

        /// <summary>牌价值口径（代币购买用——C2 定稿）：棋子 = 推导价值（EffectiveValue）；麻将 = 点数。</summary>
        private int CardValueForToken(Card card)
        {
            if (card.IsMahjong) return card.value;
            if (card.IsPiece) return _state.GetEffectiveValue(card.defId);
            return 0;
        }

        // ========== 计分统一入口（2026-08-20——唯一改 BaseScore/ScoreMultiplier/PlayerScore；各功能不再直写）==========

        /// <summary>基础分变化（击杀/相克/麻将破坏等加；扣减传负）——发 StateChanged("score")（麻将破坏 +1 也在此补发事件）。</summary>
        public void AddBaseScore(int delta)
        {
            if (delta == 0) return;
            _state.BaseScore += delta;
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "score");
        }

        /// <summary>回合结算清基础分。</summary>
        public void ClearBaseScore()
        {
            if (_state.BaseScore != 0)
            {
                _state.BaseScore = 0;
            }
        }

        /// <summary>倍率变化（升变相克 +1 / 和牌 +番数）——发 StateChanged("score")。</summary>
        public void AddMultiplier(int delta)
        {
            if (delta == 0) return;
            _state.ScoreMultiplier += delta;
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "score");
        }

        /// <summary>倍率复位 1（回合结算）。</summary>
        public void ResetMultiplier()
        {
            if (_state.ScoreMultiplier != 1)
            {
                _state.ScoreMultiplier = 1;
            }
        }

        /// <summary>本关总得分变化（结算加 / 扣分传负）——内部 clamp ≥0（总得分不下负）——发 StateChanged("score")。</summary>
        public void AddPlayerScore(int amount)
        {
            int next = _state.PlayerScore + amount;
            if (next < 0) next = 0; // clamp 0（总得分不下负——含敌方击杀扣分）
            if (next == _state.PlayerScore) return;
            _state.PlayerScore = next;
            EventCenter.Instance.EventTrigger(GameEvent.StateChanged, "score");
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
            AddPlayerScore(gained); // 统一入口（入总得分 + 发事件）
            if (waveIndex >= 0 && waveIndex < _state.WaveScores.Count)
            {
                _state.WaveScores[waveIndex] += gained; // 波次得分 = 该波次各回合结算得分之和
            }
            ClearBaseScore();
            ResetMultiplier();
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
            AddPlayerScore(-value); // 统一入口（扣本关总得分——clamp 在 AddPlayerScore 内）
        }

        /// <summary>
        /// 授予免费执行资格（2026-08-20 统一入口——OnKillTriggers 击杀触发；与 ConsumeFreeExecute 对称，Fallback 在 Resolver 内集中）。
        /// 返回是否有新增资格（同棋子只登记一次）。
        /// </summary>
        public bool GrantFreeExecute(int pieceId)
        {
            if (_state.FreeExecutes.Add(pieceId))
            {
                EventCenter.Instance.EventTrigger(GameEvent.ExtraActionGranted, pieceId);
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, pieceId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 消费一次免费执行资格（2026-08-20 统一入口——BattleFlow 不再直写 FreeExecutes）。
        /// 返回是否有资格被消费（有 → 本次执行免费）。
        /// </summary>
        public bool ConsumeFreeExecute(int pieceId)
        {
            if (_state.FreeExecutes.Remove(pieceId))
            {
                EventCenter.Instance.EventTrigger(GameEvent.BuffsChanged, pieceId); // 判定状态变化（前端刷新 buff 触发层）
                return true;
            }
            return false;
        }

        private void OnKillTriggers(PieceInstance victim, PieceInstance killer)
        {
            // 特殊能力（OnKill + ExtraAction）：【击杀者】获得免费执行资格（方案 B——不立即执行，
            // 玩家点击该棋子执行时免费；同一棋子只登记一次；有效期待策划拍板——当前保留到使用为止）
            // ⚠️ 2026-08-24 敌我边界修正：玩家棋子 → 免费资格（玩家点击执行时免费）；**敌方棋子 → 直接额外行动执行**
            // （不进入玩家资格机制——敌方回合内立即再执行一次，BattleFlow 敌方队列分流）
            if (killer != null)
            {
                foreach (var ability in killer.GetAllAbilities())
                {
                    if (ability.type == SpecialAbilityType.Trigger && ability.triggerPoint == TriggerPoint.OnKill
                        && ability.triggerEffect == TriggerEffect.ExtraAction)
                    {
                        if (killer.side == Side.Player)
                        {
                            GrantFreeExecute(killer.Id); // 统一入口（对称 ConsumeFreeExecute）
                        }
                        else
                        {
                            EventCenter.Instance.EventTrigger(GameEvent.ExtraActionGranted, killer.Id); // 敌方：立即额外行动（BattleFlow 分流）
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

        // ========== 牌区统一操作（2026-08-20 重构——Hand/DrawPile 唯一写入口；功能只调这里，禁止直碰 _state.Hand/_state.DrawPile）==========
        // 每个入口统一做：① 操作 ② 阶段不变量校验 ③ 发 HandChanged ④ 诊断（防止"分散写 → 口经不一致/漏事件/状态漂移"）
        // 牌区所有操作必须走这里——决策背景见 docs/后端待办（牌区口径统一 + 诊断）。

        /// <summary>手牌加一张牌（棋子/属性牌/麻将——统一入口；分配实例 id）。</summary>
        public void HandAddCard(Card card)
        {
            card.instanceId = card.instanceId > 0 ? card.instanceId : _state.AllocateCardId(); // 2026-08-21：入区分配实例 id
            _state.Hand.Add(card);
            LogPileWrite("HandAddCard", _state.Hand, _state.DrawPile);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>手牌移除一张该棋子牌（只删一张——防 RemoveAll 误删多张）。返回是否移除。</summary>
        public bool HandRemovePiece(int defId)
        {
            int idx = _state.Hand.FindIndex(c => c.IsPiece && c.defId == defId);
            return HandRemovePieceAt(idx);
        }

        /// <summary>按已确认索引移除手牌棋子牌，保留属性牌与实际消耗牌的一致性。</summary>
        private bool HandRemovePieceAt(int index)
        {
            if (index < 0 || index >= _state.Hand.Count || !_state.Hand[index].IsPiece) return false;
            _state.Hand.RemoveAt(index);
            LogPileWrite("HandRemovePiece", _state.Hand, _state.DrawPile);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
            return true;
        }

        /// <summary>手牌移除一张该点数麻将牌。返回是否移除。</summary>
        public bool HandRemoveMahjong(int value)
        {
            int idx = _state.Hand.FindIndex(c => c.IsMahjong && c.value == value);
            if (idx < 0) return false;
            _state.Hand.RemoveAt(idx);
            LogPileWrite("HandRemoveMahjong", _state.Hand, _state.DrawPile);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
            return true;
        }

        /// <summary>构筑整组替换手牌（DeckBuild——入口收敛；入参为棋子 defId 列表 → 转 Card；统一分配实例 id）。</summary>
        public void DeckSetHand(List<int> defIds)
        {
            _state.Hand.Clear();
            _state.Hand.AddRange(defIds.ConvertAll(id => Card.Piece(id)));
            for (int i = 0; i < _state.Hand.Count; i++)
            {
                var c = _state.Hand[i];
                c.instanceId = _state.AllocateCardId(); // 2026-08-21：构筑落账统一分配（struct 需索引写回）
                _state.Hand[i] = c;
            }
            LogPileWrite("DeckSetHand", _state.Hand, _state.DrawPile);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>抽牌堆随机抽一张入黑牌（2026-08-20：随机——策划确认；抽牌堆空 → null）。</summary>
        public Card? DrawFromPile()
        {
            if (_state.DrawPile == null || _state.DrawPile.Count == 0)
            {
                return null;
            }
            int idx = RandomManager.Instance.Range(0, _state.DrawPile.Count);
            var card = _state.DrawPile[idx];
            _state.DrawPile.RemoveAt(idx);
            _state.Hand.Add(card);
            LogPileWrite("DrawFromPile", _state.Hand, _state.DrawPile);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
            return card;
        }

        /// <summary>抽牌堆加一张（麻将 18 张进堆/转移用——统一写 DrawPile 的入口；分配实例 id）。</summary>
        private void DrawPileAdd(Card card)
        {
            card.instanceId = card.instanceId > 0 ? card.instanceId : _state.AllocateCardId(); // 2026-08-21：入区分配
            _state.DrawPile.Add(card);
        }

        /// <summary>牌区写诊断（2026-08-20：每个统一入口调用——阶段 + Hand/DrawPile 数量——出问题时前端可对照；待办第 2 条）。</summary>
        private void LogPileWrite(string caller, List<Card> hand, List<Card> pile)
        {
            // 准备/摆放阶段（Placement）不变量：手牌只应含初始棋子牌（编辑跨档后才暴露的问题——待办第 1 条）
            if (_state.Phase == BattlePhase.Placement)
            {
                foreach (var c in hand)
                {
                    if (c.IsPiece && _state.GetEffectiveType(c.defId) != PieceType.Initial)
                    {
                        Debug.LogWarning($"[牌区] {caller}：准备阶段手牌含非初始牌 {c.defId}（EffectiveType={_state.GetEffectiveType(c.defId)}）——检查牌区分区口径");
                    }
                }
            }
            // 2026-08-21：牌实例 id 唯一性断言（分配漏了 = 出现 0/重复——排查信号）
            var seen = new HashSet<int>();
            foreach (var list in new List<Card>[] { hand, pile })
            {
                foreach (var c in list)
                {
                    if (c.instanceId <= 0)
                    {
                        Debug.LogWarning($"[牌区] {caller}：牌缺少实例 id（defId={c.defId} value={c.value}）——统一入口未分配？");
                    }
                    else if (!seen.Add(c.instanceId))
                    {
                        Debug.LogWarning($"[牌区] {caller}：牌实例 id 重复（{c.instanceId}）——分配/读档重分配异常");
                    }
                }
            }
        }

        // ========== 事件/编辑/加牌效果（经 Resolver 落账——唯一写入口）==========

        /// <summary>编辑程序落账（实时编辑/事件 EditProgram——改种类级表）。
        /// ⚠️ 2026-08-23 决策 3 撤销：同 id 唯一校验移除（策划定案允许跨层叠加——程序内同 id 可多份；
        /// "同层不重复"由消耗制候选保证）。消耗维护在唯一写入口（Undo/还原/内置回位全部自动同步——撤销后候选恢复）。
        /// </summary>
        public void ApplyProgramEdit(int defId, List<Template> program)
        {
            _state.CurrentPrograms.TryGetValue(defId, out var before); // 相对"已落账程序"的增量（未编辑过=null）
            UpdateConsumedModules(before, program);
            _state.CurrentPrograms[defId] = program;
            EventCenter.Instance.EventTrigger(GameEvent.ProgramEdited, defId);
        }

        /// <summary>本层消耗（净增量）维护（2026-08-23 决策 4 定案"池子构成规则"）：候选池 = 模板库 − 本层占用增量。
        /// 对涉及的外部模块 key：delta = after 计数 − before 计数——正（放入）计入消耗（候选消失）；
        /// 负（撤销/移除）抵消（=0 移除键——候选恢复）；进层（TowerFlow.EnterFloor 清空 ConsumedModules）→ 跨层复原。</summary>
        private void UpdateConsumedModules(List<Template> before, List<Template> after)
        {
            if (_state.ConsumedModules == null || after == null) return;
            var keys = new List<string>();
            CollectExternalKeys(before, keys);
            CollectExternalKeys(after, keys);
            foreach (var key in keys)
            {
                int delta = CountExternalOf(after, key) - CountExternalOf(before, key);
                if (delta == 0) continue;
                if (_state.ConsumedModules.TryGetValue(key, out var cur))
                {
                    cur += delta;
                    if (cur <= 0) _state.ConsumedModules.Remove(key);
                    else _state.ConsumedModules[key] = cur;
                }
                else if (delta > 0)
                {
                    _state.ConsumedModules[key] = delta;
                }
            }
        }

        private static void CollectExternalKeys(List<Template> list, List<string> keys)
        {
            if (list == null) return;
            foreach (var t in list)
            {
                var key = ExternalModuleKey(t);
                if (key != null && !keys.Contains(key)) keys.Add(key);
            }
        }

        private static int CountExternalOf(List<Template> list, string key)
        {
            int count = 0;
            if (list == null) return count;
            foreach (var t in list)
            {
                if (ExternalModuleKey(t) == key) count++;
            }
            return count;
        }

        /// <summary>外部模块 key（候选池消耗规则单一来源——EditorSession 过滤共用）：类型名:id；
        /// 内置模块（默认程序槽）或 id=0 返回 null（不参与消耗）。</summary>
        public static string ExternalModuleKey(Template t)
        {
            if (t == null || t.id <= 0 || IsBuiltinModule(t)) return null;
            return $"{t.GetType().Name}:{t.id}";
        }

        /// <summary>内置模块判定（规则单一来源——编号体系：内置 Move≤9 / Attack≤11 / Effect≤3；其余为外部）。</summary>
        public static bool IsBuiltinModule(Template t)
        {
            switch (t)
            {
                case MoveTemplate move: return move.id > 0 && move.id <= 9;
                case AttackTemplate attack: return attack.id > 0 && attack.id <= 11;
                case EffectTemplate effect: return effect.id > 0 && effect.id <= 3;
                default: return false;
            }
        }

        /// <summary>玩家手牌加牌（事件 AddPiece 效果；2026-08-20 统一入口——棋子牌入牌）。</summary>
        public void AddToHand(int defId)
        {
            HandAddCard(Card.Piece(defId)); // 2026-08-20 统一牌区入口
        }

        /// <summary>玩家手牌加牌（带属性——属性玩法复制牌"属性相同"；麻将牌非棋子不带属性请用 AddMahjongToHand）。</summary>
        public void AddToHand(Card card)
        {
            HandAddCard(card); // 2026-08-20 统一牌区入口
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

            // 通过校验：整组替换手牌（落账纪律——统一牌区入口 DeckSetHand）
            // ⚠️ 2026-08-13：按入参顺序写入（原 AddRange(seen) 迭代 HashSet——顺序不确定；
            // 去重校验仍由 seen 完成，顺序按玩家选择顺序——存档往返一致）
            // ⚠️ 2026-08-20 牌结构：构筑只选棋子牌 → 转 Card（无属性）
            DeckSetHand(effective); // 顺序 = 入参顺序（可复数模式下含重复）
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
                bool hasMahjong = false;
                foreach (var card in _state.DrawPile)
                {
                    if (card.IsMahjong) { hasMahjong = true; break; }
                }
                if (!hasMahjong)
                {
                    foreach (var tile in Mahjong.Tiles())
                    {
                        DrawPileAdd(tile); // 统一写 DrawPile
                    }
                }
            }
            for (int i = _state.Hand.Count - 1; i >= 0; i--)
            {
                var card = _state.Hand[i];
                // 非初始棋子牌转抽牌堆（麻将牌 IsMahjong 亦转——本应直接进抽牌堆，此处防御）；
                // 初始棋子牌已由 Placement 阶段全部署（校验兜底）
                if (!card.IsPiece || _state.GetEffectiveType(card.defId) != PieceType.Initial)
                {
                    DrawPileAdd(card); // 统一写 DrawPile
                    _state.Hand.RemoveAt(i);
                }
            }
            LogPileWrite("SetupDrawPile", _state.Hand, _state.DrawPile);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>抽 1 张（2026-08-20：从抽牌堆随机抽一张 → 手牌；统一牌区入口 DrawFromPile；抽牌堆空 = 返回 null）。
        /// ⚠️ 2026-08-23 E5：返回抽到的牌（调用方按需检查触发——如 BattleFlow 的"抽到编辑牌"检测）。</summary>
        public Card? DrawCard()
        {
            return DrawFromPile();
        }

        /// <summary>敌方波次池增强（加牌落点：敌方无手牌——增强未来波次阵容）。</summary>
        public void AddToEnemyWavePool(int defId)
        {
            _state.AddToEnemyWavePool(defId);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, null);
        }

        /// <summary>获得遗物（事件 GrantRelic 效果——整局持续、可叠加）。2026-08-22：应用能力基础效果（APBonus 即时生效）。</summary>
        public void AddRelic(string relicName)
        {
            var relic = ConfigTable.FindByName<RelicDef>(relicName);
            if (relic == null)
            {
                Core.Assert.Fail($"GrantRelic: 找不到遗物资产 {relicName}");
                return;
            }
            _state.Relics.Add(relic);
            // 能力基础效果（2026-08-22）：APBonus 获得时即时生效（PlayerAPMax += N）
            foreach (var e in relic.effects)
            {
                if (e != null && e.type == RelicEffectType.APBonus)
                {
                    _state.PlayerAPMax += e.value;
                }
            }
            EventCenter.Instance.EventTrigger(GameEvent.RelicObtained, relic);
        }

        // ========== 能力事件（2026-08-22——策划案：按玩法词条过滤能力池→随机3→每项可刷新一次→三选一）==========

        /// <summary>当前玩法词条（2026-08-22）：第 1 层无玩法 = "basic"（初始玩法）；后续玩法（麻将/属性）按对应词条。</summary>
        private List<string> CurrentAbilityTags()
        {
            var tags = new List<string>();
            if (_state.ActiveStyles == null || _state.ActiveStyles.Count == 0)
            {
                tags.Add("basic");
            }
            else
            {
                foreach (var s in _state.ActiveStyles)
                {
                    tags.Add(s); // "mahjong"/"element"——与遗物 tags 对应
                }
            }
            return tags;
        }

        /// <summary>能力池 = 全部遗物中词条匹配当前玩法 且 未被持有 的（候选排除已持有——不重复拿同一能力）。
        /// ⚠️ 2026-08-24 复合词条（D1 定稿）：过滤改"**全部匹配**"——遗物 tags ⊆ 有效词条集（当前玩法词条 ∪ {basic}——basic 永远匹配）；无词条遗物（旧测试遗物）不参与。</summary>
        private List<RelicDef> AbilityPool()
        {
            var tags = CurrentAbilityTags();
            var effective = new List<string>(tags);
            if (!effective.Contains("basic")) effective.Add("basic"); // D1：basic 永远匹配（基础能力池始终可用）
            var pool = new List<RelicDef>();
            foreach (var relic in ConfigTable.All<RelicDef>())
            {
                if (relic == null || relic.tags == null || relic.tags.Count == 0) continue; // 无词条（旧测试遗物）不参与
                bool allMatch = true;
                foreach (var t in relic.tags)
                {
                    if (!effective.Contains(t)) { allMatch = false; break; }
                }
                if (!allMatch) continue;
                if (_state.Relics.Contains(relic)) continue; // 排除已持有
                pool.Add(relic);
            }
            return pool;
        }

        /// <summary>抽取能力候选（事件打开时调用——能力事件 isAbilityPick）：池中随机 3 不重复进展示集；发 AbilityCandidatesDrawn。</summary>
        public void DrawAbilityCandidates()
        {
            var pool = AbilityPool();
            var picked = new List<RelicDef>();
            var copy = new List<RelicDef>(pool);
            while (picked.Count < 3 && copy.Count > 0)
            {
                int idx = RandomManager.Instance.Range(0, copy.Count);
                picked.Add(copy[idx]);
                copy.RemoveAt(idx);
            }
            _state.AbilityCandidates = picked;
            _state.AbilityRefreshLeft = new List<int>();
            for (int i = 0; i < picked.Count; i++)
            {
                _state.AbilityRefreshLeft.Add(1); // 每项 1 次刷新
            }
            EventCenter.Instance.EventTrigger(GameEvent.AbilityCandidatesDrawn, _state.AbilityCandidates);
        }

        /// <summary>
        /// 刷新候选（2026-08-22 池循环算法）：优先从「池 − 展示集」抽（已展示过的一律不抽）；
        /// 候选空 → 回填（池全部 − 当前被刷旧项——至少不回当前项）；新项进展示集；每项刷新消费 1 次。
        /// </summary>
        public void RefreshAbilityCandidate(int choiceIndex)
        {
            if (choiceIndex < 0 || choiceIndex >= _state.AbilityCandidates.Count) return;
            if (_state.AbilityRefreshLeft == null || choiceIndex >= _state.AbilityRefreshLeft.Count || _state.AbilityRefreshLeft[choiceIndex] <= 0)
            {
                return; // 刷新次数用尽
            }
            var old = _state.AbilityCandidates[choiceIndex];
            var pool = AbilityPool();
            // 候选 = 池 − 展示集（已展示过的一律不抽）
            var candidates = new List<RelicDef>();
            foreach (var r in pool)
            {
                if (!_state.AbilityCandidates.Contains(r)) candidates.Add(r);
            }
            if (candidates.Count == 0)
            {
                // 回填：池全部 − 当前被刷旧项（至少不回当前项）
                candidates = new List<RelicDef>();
                foreach (var r in pool)
                {
                    if (r != old) candidates.Add(r);
                }
            }
            if (candidates.Count == 0)
            {
                return; // 池真空（极端——无可刷）
            }
            var replacement = candidates[RandomManager.Instance.Range(0, candidates.Count)];
            _state.AbilityCandidates[choiceIndex] = replacement;
            _state.AbilityRefreshLeft[choiceIndex]--;
            EventCenter.Instance.EventTrigger(GameEvent.AbilityCandidatesDrawn, _state.AbilityCandidates);
        }

        /// <summary>选择能力候选（三选一落账）：GrantRelic（含 APBonus 应用）→ 清候选 → 事件完成（推进流程）。</summary>
        public void SelectAbility(int choiceIndex)
        {
            if (choiceIndex < 0 || choiceIndex >= _state.AbilityCandidates.Count) return;
            var relic = _state.AbilityCandidates[choiceIndex];
            AddRelic(relic.name); // 统一遗物落账（含 APBonus）
            _state.AbilityCandidates.Clear();
            _state.AbilityRefreshLeft.Clear();
            EventCenter.Instance.EventTrigger(GameEvent.EventCompleted, _state.CurrentEventId); // 事件完成（推进）
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
            // 2026-08-23 回放增强：补全上下文（side/defId/turn）——离线推演直读（此前只记 pieceId，棋子死后无法反查）
            action.turn = _state.TurnCount;
            int pieceId = -1;
            if (action is MoveAction moveAct) pieceId = moveAct.pieceId;
            else if (action is AttackAction atkAct) pieceId = atkAct.pieceId;
            else if (action is PromoteAction promoAct) pieceId = promoAct.pieceId;
            else if (action is SkipAction skipAct) pieceId = skipAct.pieceId;
            // DeployAction/DeathAction：无 pieceId（side/defId 由各自构造/记录时填入——见 Actions.cs 2026-08-23 注释）
            if (pieceId > 0 && _state.PiecesById.TryGetValue(pieceId, out var actPiece))
            {
                action.side = actPiece.side;
                action.defId = actPiece.DefId;
            }
            _state.ReplayLog.Add(action); // 回放记录（数据）
            Debug.Log($"[Resolver] 落账: {action.GetType().Name}"); // 落账日志（现场还原）
        }

        /// <summary>排查诊断（2026-08-23 第二梯队）：统一走 GameState.LogDiagnostic——开关判定与环形上限在唯一写入口。</summary>
        private void TraceBattle(string message)
        {
            _state.LogDiagnostic(message);
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
        public AttackMode AttackMode;   // 2026-08-23：本次攻击类型透传（前端攻击音分发契约——李毕待办；单体/AOE 命中/空放三处已填）
    }

    public class DeployInfo
    {
        public int PieceId;
        public int DefId;
        public Side Side;
        public Vector2Int Cell;
        public int CardInstanceId; // 2026-08-21：消耗的牌实例（前端知道"哪张被打出"）
    }

    public class PromoteInfo
    {
        public int PieceId;
        public int NewDefId;
        public int CardInstanceId; // 2026-08-21：消耗的升变牌实例
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
