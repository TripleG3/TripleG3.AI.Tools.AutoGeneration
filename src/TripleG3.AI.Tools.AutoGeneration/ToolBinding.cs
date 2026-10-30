using System.Runtime.ExceptionServices;
using TripleG3.Cis;

namespace TripleG3.AI.Tools.AutoGeneration;

public delegate ValueTask<object?> ToolInvocationHandler(ToolCall call, ToolArguments arguments, CancellationToken cancellationToken);

/// <summary>One host-owned execution lane and observable invocation state for a tool.</summary>
public sealed class ToolBinding : IAsyncDisposable
{
    private GeneratedTool? tool;
    private readonly ToolResultFormat format;
    private readonly InvocationStateService state = new();
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private ToolInvocationHandler? handler;
    private TaskCompletionSource idle = Completed();
    private int pending;
    private bool closing;
    private Task? disposal;

    internal ToolBinding(GeneratedTool tool, ToolInvocationHandler handler, ToolResultFormat format)
    {
        this.tool = tool;
        this.handler = handler;
        this.format = format;
        Descriptor = tool.Describe(format);
        lifetimeToken = lifetime.Token;
    }

    public ToolDescriptor Descriptor { get; }
    public State<ToolInvocationState> State => state.State;
    public event EventHandler<State<ToolInvocationState>>? StateChanged
    {
        add => state.StateChanged += value;
        remove => state.StateChanged -= value;
    }

    public async ValueTask<ToolInvocationResult> InvokeAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        ToolInvocationHandler invoke;
        GeneratedTool definition;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            invoke = handler!;
            definition = tool!;
            if (pending++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
            ExceptionDispatchInfo? failure = null;
            string serialized = string.Empty;
            ToolArguments? arguments = null;
            State<ToolInvocationState> result = await state.SetAsync(async token =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    definition.ValidateCall(call);
                    arguments = ToolArgumentBinder.Bind(definition, call, token);
                    DateTimeOffset started = DateTimeOffset.UtcNow;
                    object? output = await invoke(call, arguments, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    serialized = definition.SerializeResult(output, format);
                    return ToolInvocationState.Completed(definition,
                        ResponseItem.CreateFunctionCallItem(call.CallId, call.Name, BinaryData.FromString(arguments.EffectiveArgumentsJson)),
                        output, started, DateTimeOffset.UtcNow);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                    throw;
                }
            }, linked.Token).ConfigureAwait(false);
            failure?.Throw();
            linked.Token.ThrowIfCancellationRequested();
            ToolInvocationState value = GeneratedTool.GetReadyInvocationValueOrThrow(result);
            return new(call.CallId, call.Name, value.Result, serialized, value)
            { EffectiveArgumentsJson = arguments!.EffectiveArgumentsJson };
        }
        finally
        {
            lock (gate)
            {
                if (--pending == 0) idle.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closing = true;
            disposal = Task.Run(DisposeCoreAsync);
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? cancellationFailure = null;
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { cancellationFailure = exception; }
        Task completion;
        lock (gate) completion = idle.Task;
        await completion.ConfigureAwait(false);
        handler = null;
        tool = null;
        await state.SetAsync(_ => ValueTask.FromResult(ToolInvocationState.Empty), CancellationToken.None).ConfigureAwait(false);
        lifetime.Dispose();
        if (cancellationFailure is not null) ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
    }

    private static TaskCompletionSource Completed()
    {
        TaskCompletionSource result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    private sealed class InvocationStateService() : StateService<ToolInvocationState>(() => ToolInvocationState.Empty);
}
