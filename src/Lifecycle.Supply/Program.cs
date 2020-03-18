using System.Linq;

namespace Lifecycle
{
    class Program
    {
        static int Main(string[] args)
        {
            var argsWithCommand = new[] {"Supply"}.Concat(args).ToArray();
            return WindowsServicesBuildpack.Program.Main(argsWithCommand);
        }
    }
}