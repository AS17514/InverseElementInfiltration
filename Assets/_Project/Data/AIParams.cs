using TheLaw.Core;

namespace TheLaw.Data
{
    /// <summary>
    /// 敌方 AI 决策参数（SO 资产，数据层）——短视吃子至上算法的权重配置。
    /// </summary>
    public class AIParams : GameConfigBase
    {
        public bool greedyCapture = true;   // 短视吃子开关（优先击杀收益最高的目标）
        public int moveScoreWeight = 1;     // 移动收益权重
        public int attackScoreWeight = 10;  // 攻击（吃子）收益权重
        public TargetRule targetRule = TargetRule.HighestValue; // 目标选择规则
    }
}
