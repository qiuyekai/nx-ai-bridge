// tools: user_error, crash, nx_error, api_mismatch
// Test handler: every kind of failure the dispatcher must map to an error code.
using System;
using System.Collections.Generic;

namespace NxAiBridge.Handlers
{
    public class ErrorsTool : ITool
    {
        public object Run(string tool, Args a)
        {
            if (tool == "user_error") throw new ToolError("INVALID_PARAMS", "bad value", "use a positive number");
            if (tool == "nx_error") throw new NXOpen.NXException(670030, "Tool body completely outside target body.");
            if (tool == "api_mismatch") return Dyn.Call("some string", "NoSuchMethod", 1, "two");
            throw new InvalidOperationException("boom");
        }
    }
}
