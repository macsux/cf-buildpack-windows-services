using System.Linq;

namespace Lifecycle
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var argsWithCommand = new[] {"Run"}.Concat(args).ToArray();
            return WindowsServicesBuildpack.Program.Main(argsWithCommand);
        }
    }
}