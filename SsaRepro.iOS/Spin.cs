using System.Net.WebSockets;
using System.Threading;

// Standalone repro for dotnet/runtime#126448 — Mono interpreter SSA crash on .NET 10 iOS.
// Requires UseInterpreter=true (see the .csproj): the fault is in the interpreter's SSA pass.
//
// N workers spin a tight `await Task.Yield()` loop keeping ONE ClientWebSocket alive; a task
// aborts that socket every SSA_ABORT_MS ms, so the workers keep re-running the connect path
// against a real remote host. The thread pool injects fresh threads to service the continuations,
// and each fresh thread's first work item runs through start_wrapper -> interp_runtime_invoke —
// the frame where the race hits.
//
// A REAL remote host is required (a loopback/local server does not reproduce). Knobs (env):
//   SSA_WS_SERVER  host (default static.tildacdn.com) -> <scheme>://<host>/ws/
//   SSA_WS_SCHEME  wss (default) or ws
//   SSA_SPIN_WORKERS  workers (default 8)
//   SSA_ABORT_MS   abort the socket every N ms (default 2000; 0 = never)
public static class Spin
{
	static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

	static ClientWebSocket socket;
	static CancellationTokenSource socketCts;

	static int Workers => EnvInt("SSA_SPIN_WORKERS", 8);
	static int AbortMs => EnvInt("SSA_ABORT_MS", 2000);
	static string Scheme => Environment.GetEnvironmentVariable("SSA_WS_SCHEME") ?? "wss";
	static string Host => Environment.GetEnvironmentVariable("SSA_WS_SERVER") ?? "static.tildacdn.com";

	public static void Start()
	{
		var workers = Workers;
		Console.WriteLine($"[spin] {workers} workers -> {Scheme}://{Host}/ws/, abort={AbortMs} ms");

		ThreadPool.SetMinThreads(Math.Max(workers + 2, 8), Math.Max(workers + 2, 8));

		for (var i = 0; i < workers; i++)
			_ = SocketWorker();

		_ = AbortLoop();
	}

	static async Task SocketWorker()
	{
		while (true)
		{
			try
			{
				await EnsureConnectedAsync();
			}
			catch
			{
			}

			await Task.Yield();
		}
	}

	static async Task EnsureConnectedAsync()
	{
		if (socket?.State == WebSocketState.Open)
			return;

		await gate.WaitAsync();
		try
		{
			if (socket?.State == WebSocketState.Open)
				return;

			socketCts?.Cancel();
			socketCts?.Dispose();
			socketCts = new CancellationTokenSource();

			socket?.Dispose();
			socket = new ClientWebSocket();
			socket.Options.SetRequestHeader("Authorization", "Bearer test");

			await socket.ConnectAsync(new Uri($"{Scheme}://{Host}/ws/"), socketCts.Token);
		}
		finally
		{
			gate.Release();
		}
	}

	static async Task AbortLoop()
	{
		var ms = AbortMs;
		if (ms <= 0)
			return;

		while (true)
		{
			await Task.Delay(ms);

			try
			{
				socket?.Abort();
			}
			catch
			{
			}
		}
	}

	static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
