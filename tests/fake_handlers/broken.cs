// tools: compile_demo, always_broken
// variant: 1
// Test handler: does not compile on purpose.
using System;
namespace NxAiBridge.Handlers
{
    public class Broken : ITool
    {
        public object Run(string tool, Args a) { return ThisMethodDoesNotExist(42); }
    }
}
