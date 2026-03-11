using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace YK_SCADA.Tools
{
    public static class Global
    {
        public static ConcurrentDictionary<string, bool> Bdic = new ConcurrentDictionary<string, bool>();
        public static ConcurrentDictionary<string, string> Sdic = new ConcurrentDictionary<string, string>();







        static Global()
        {
            Bdic["wifi"] = true;
     
            Sdic["name"] = "Vitorio";

        }
    }
}