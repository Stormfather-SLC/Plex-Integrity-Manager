using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PIM.Core.Models
{
    public class ScanProgress
    {
        public int Total { get; set; }
        public int Processed { get; set; }
        public string CurrentFile { get; set; } = "";
        public bool IsRunning { get; set; }
    }
}
