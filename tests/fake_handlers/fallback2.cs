// tools: fallback_demo, compile_demo
// variant: 2
// Test handler: the working fallback.
using System;
namespace NxAiBridge.Handlers
{
    public class Fallback2 : ITool
    {
        public object Run(string tool, Args a) { return R.Obj("served_by", "variant 2", "tool", tool); }
    }
}
