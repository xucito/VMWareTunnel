using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace VMWareHypervisorTunnel
{
    public static class Utility
    {
        static public object GetValObjDy(this object obj, string propertyName)
        {
            return obj.GetType().GetProperty(propertyName).GetValue(obj, null);
        }
    }
}