using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Serilog;

public sealed class LogFunctionCallsMiddleware
{
    public async ValueTask<object?> InvokeAsync(AIAgent inner, FunctionInvocationContext context, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        Log.Information("[Function Call] {0}", context.Function.Name);
        var response = await next(context, ct);
        Log.Information("[Function Response] {0}", response);
        return response;
    }
}