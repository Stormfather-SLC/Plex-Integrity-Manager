using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PIM.Core.Models;

namespace PIM.Core.Interfaces
{
    public interface IFileNameParser
    {
        void Parse(Movie movie);
    }
}