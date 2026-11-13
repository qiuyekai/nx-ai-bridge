// tools: echo, nx_status
// Test handler: returns its parameters.
using System;
using System.Collections.Generic;

namespace NxAiBridge.Handlers
{
    public class EchoTool : ITool
    {
        public object Run(string tool, Args a)
        {
            return R.Obj("tool", tool, "params", a.Map, "mode", Dispatcher.Mode, "connected", true,
                "number", a.Num("number", 1.5), "list", R.Nums(1, 2.25, 3));
        }
    }
}
