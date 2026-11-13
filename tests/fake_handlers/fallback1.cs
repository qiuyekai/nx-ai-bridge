// tools: fallback_demo
// variant: 1
// Test handler: fails at run time, so variant 2 must take over.
using System;
namespace NxAiBridge.Handlers
{
    public class Fallback1 : ITool
    {
        public object Run(string tool, Args a)
        {
            if (a.Bool("user_error", false)) throw new ToolError("NOT_FOUND", "nothing there");
            return Dyn.Get(new object(), "MissingProperty");
        }
    }
}
