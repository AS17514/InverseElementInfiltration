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
            if (target != null)
            {
                died = ModifyDurability(target, -damage, piece); // killer = 攻击者
            }

            // 附着结算（Attach/OnAttack：如十字额外伤害——范围额外结算）
            ResolveAttachOnAttack(piece, action.targetCell, action.template, damage);

            // 发事件（UI 表现依据：攻击者/目标/伤害/是否死亡）
            EventCenter.Instance.EventTrigger(GameEvent.DamageDealt, new DamageInfo
            {
                AttackerId = piece.Id,
                TargetId = targetId,
                TargetCell = action.targetCell,
                Damage = damage,
                TargetDied = died,
                FriendlyFire = action.template.friendlyFire,
            });
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
            if (action.side == Side.Player)
            {
                _state.Hand.Remove(action.pieceDefId); // 玩家部署：手牌打出
            }
            _state.Pieces[action.cell] = piece;
            _state.PiecesById[piece.Id] = piece;
            EventCenter.Instance.EventTrigger(GameEvent.PieceDeployed, new DeployInfo { PieceId = piece.Id, DefId = action.pieceDefId, Side = action.side, Cell = action.cell });
        }

        private void ResolvePromote(PromoteAction action)
        {
            var piece = _state.GetPiece(action.pieceId);
            if (piece == null)
            {
                return;
            }
            var newDef = ConfigTable.Get<PieceDef>(action.newDefId);
            piece.def = newDef;
            piece.ApplyDefProperties(); // 承伤+护盾按新身体重算（2026-08-12：护盾此前漏算——升变丢新身体护盾；统一初始化路径）
            if (piece.side == Side.Player)
            {
                _state.Hand.Remove(action.newDefId); // 升变牌打出（手牌减一）——仅玩家（敌方无手牌）
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

            // 击杀积分（价值分）+ 墓地（仅玩家棋子；敌方无手牌概念）+ 每波得分累计
            if (victim.side == Side.Player)
            {
                _state.Graveyard.Add(victim.DefId);
                _state.EnemyScore += victim.def.value;
            }
            else
            {
                _state.PlayerScore += victim.def.value;
                if (victim.waveIndex >= 0 && victim.waveIndex < _state.WaveScores.Count)
                {
                    _state.WaveScores[victim.waveIndex] += victim.def.value; // 每波得分累计（第 3 关"每波达标"）
                }
            }

            // OnKill 触发点（层差异 + 遗物 + 特殊能力——动作进待执行队列）
            OnKillTriggers(victim, killer);

            EventCenter.Instance.EventTrigger(GameEvent.PieceDied, new DeathInfo { PieceId = victim.Id, Side = victim.side, KillerId = killer != null ? killer.Id : -1 });
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

        /// <summary>玩家手牌加牌（事件 AddPiece 效果）。</summary>
        public void AddToHand(int defId)
        {
            _state.Hand.Add(defId);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
        }

        /// <summary>
        /// 牌组构筑落账（DeckBuild 事件——整组替换手牌，含牌数/总价值校验）。
        /// 限制来自当前事件定义（EventDefinition.deckSizeLimit/totalValueLimit；0 = 不限制）。
        /// 校验失败返回 false 且不改状态（UI 提示后保持面板编辑态）。
        /// </summary>
        public bool BuildDeck(List<int> defIds)
        {
            // ⚠️ 2026-08-12：空牌组校验（下限型——原校验全是上限型，空列表通过 → 手牌清空 → 无棋无牌开局即败）
            if (defIds == null || defIds.Count == 0)
            {
                return false; // 至少 1 张——规则层兜底，不依赖 UI
            }
            // 去重校验（同种棋子一张——手牌按 defId 唯一）
            var seen = new HashSet<int>();
            foreach (var id in defIds)
            {
                if (!seen.Add(id))
                {
                    return false;
                }
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
            int sizeLimit = ev.deckSizeLimit;
            int valueLimit = ev.totalValueLimit;

            int totalValue = 0;
            foreach (var id in seen)
            {
                var def = ConfigTable.Find<PieceDef>(id);
                if (def == null)
                {
                    return false; // 牌组含未知棋子——配置缺失当场拒绝
                }
                totalValue += def.value;
            }
            if (sizeLimit > 0 && seen.Count > sizeLimit) return false;
            if (valueLimit > 0 && totalValue > valueLimit) return false;

            // 通过校验：整组替换手牌（落账纪律——唯一写入口）
            _state.Hand.Clear();
            _state.Hand.AddRange(seen);
            EventCenter.Instance.EventTrigger(GameEvent.HandChanged, _state.Hand);
            return true;
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
