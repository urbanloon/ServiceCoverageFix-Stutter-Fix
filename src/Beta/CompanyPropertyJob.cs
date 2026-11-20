using Game.Buildings;
using Unity.Burst;
using Unity.Jobs;
namespace ServiceCoverageFix
{
    [BurstCompile]
    internal struct CompanyPropertyJob : IJob
    {
        public PropertyUtils.CompanyFindPropertyJob Source;

        public void Execute()
        {
            CompanySearchBody.Execute(ref Source);
        }
    }
}
