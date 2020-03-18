namespace WindowsServicesBuildpack
{
    public abstract class SupplyBuildpack : BuildpackBase
    {
        public sealed override void Supply(string buildPath, string cachePath, string depsPath, int index)
        {
            DoApply(buildPath, cachePath, depsPath, index);
        }


        // supply buildpacks may get this lifecycle event, but since only one buildpack will be selected if detection is used, it must be final
        // therefore supply buildpacks always must reply with false
        public sealed override bool Detect(string buildPath) => false;  
    }
}