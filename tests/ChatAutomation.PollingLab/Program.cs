using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAutomation.Browser;
using ChatAutomation.Domain;

namespace ChatAutomation.PollingLab;

// Executável de teste isolado. Nenhum envio ocorre no modo padrão (probe).
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = LabOptions.Parse(args);
            if (options.Mode == "self-test")
            {
                SelfTest();
                Print(new { status = "PASS", test = "idle gate and transition safety" });
                return 0;
            }

            using var web = new WebInterface();
            var chat = web.GetChat(options.Chat);
            if (options.Mode == "probe")
            {
                RunProbe(chat, options);
                return 0;
            }

            if (!options.AllowSend)
                throw new InvalidOperationException(
                    "SEND_NOT_ARMED: acrescente --allow-send para autorizar 3 mensagens reais.");
            RunThreeMessages(chat, options);
            return 0;
        }
        catch (Exception ex)
        {
            Print(new { status = "BLOCKED", error = ex.Message,
                note = "Nenhuma tentativa de envio é repetida automaticamente." });
            return 2;
        }
    }

    private static void RunProbe(Chat chat, LabOptions options)
    {
        var calls = new List<double>();
        var intervals = new List<double>();
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        var transitions = new List<object>();
        int trueCount = 0, falseCount = 0;
        bool? previous = null;
        long? previousTick = null;
        using var currentProcess = Process.GetCurrentProcess();
        var startCpu = currentProcess.TotalProcessorTime.TotalMilliseconds;
        var startEdgeCpu = EdgeCpu();
        var total = Stopwatch.StartNew();

        while (total.Elapsed.TotalSeconds < options.DurationSeconds)
        {
            var started = Stopwatch.GetTimestamp();
            if (previousTick is { } old)
                intervals.Add(Stopwatch.GetElapsedTime(old, started).TotalMilliseconds);
            previousTick = started;
            bool? busy = null;
            try
            {
                busy = chat.IsBusy(); // Mesmo objeto/UIA; sem criar processo a cada leitura.
                if (busy.Value) trueCount++;
                else falseCount++;
                if (previous != busy)
                    transitions.Add(new { elapsedMs = Math.Round(total.Elapsed.TotalMilliseconds, 2),
                        isBusy = busy.Value });
                previous = busy;
            }
            catch (Exception ex)
            {
                previous = null;
                var reason = ex.GetType().Name + ": " + ex.Message;
                errors[reason] = errors.GetValueOrDefault(reason) + 1;
            }
            calls.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            SleepToMinimumInterval(started, options.IntervalMs);
        }

        var elapsedMs = total.Elapsed.TotalMilliseconds;
        var processCpuMs = currentProcess.TotalProcessorTime.TotalMilliseconds - startCpu;
        var endEdgeCpu = EdgeCpu();
        var edgeCpuMs = startEdgeCpu.Keys.Intersect(endEdgeCpu.Keys)
            .Sum(id => Math.Max(0, endEdgeCpu[id] - startEdgeCpu[id]));
        Print(new
        {
            status = "PROBE_COMPLETE",
            chat = chat.Name,
            intervalRequestedMs = options.IntervalMs,
            elapsedMs = Math.Round(elapsedMs, 2),
            samples = calls.Count,
            busyTrue = trueCount,
            busyFalse = falseCount,
            errors,
            transitions,
            achievedSamplesPerSecond = Math.Round(calls.Count * 1000.0 / elapsedMs, 2),
            uiaCallMs = Statistics(calls),
            sampleStartGapMs = Statistics(intervals),
            processCpuMs = Math.Round(processCpuMs, 2),
            processCpuPercentOfOneCore = Math.Round(100 * processCpuMs / elapsedMs, 2),
            edgeCpuMsApprox = Math.Round(edgeCpuMs, 2),
            caveat = "CPU do Edge inclui outras abas/processos; não é custo causal isolado. UIA não consome tokens de API."
        });
    }

    private static void RunThreeMessages(Chat chat, LabOptions options)
    {
        // Impede duas instâncias deste laboratório de enviarem simultaneamente
        // para a mesma URL. Não bloqueia ações manuais no Edge.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chat.Url)))[..20];
        using var gate = new Mutex(false, "Local\\ChatAutomation.PollingLab." + key);
        if (!gate.WaitOne(0))
            throw new InvalidOperationException("LAB_ALREADY_RUNNING: já existe um teste para esta conversa.");

        try
        {
            var pinned = chat.SetFocus();
            if (string.IsNullOrWhiteSpace(pinned.TabRuntimeId))
                throw new InvalidOperationException(
                    "TAB_RUNTIME_ID_MISSING: não é possível distinguir cópias da mesma URL com segurança.");
            Print(new { status = "ARMED_THREE_MESSAGES", chat = chat.Name,
                url = chat.Url, pinned.Handle, pinned.TabIndex, pinned.TabRuntimeId,
                intervalRequestedMs = options.IntervalMs, stableIdleMs = options.StableIdleMs,
                messages = 3, note = "Envios reais, um por vez; sem retry após clique." });

            var idle = WaitForIdle(chat, options, requireBusy: false);
            for (var i = 1; i <= 3; i++)
            {
                EnsureSameTab(chat, pinned);
                var draft = chat.GetText();
                if (!string.IsNullOrEmpty(draft))
                    throw new InvalidOperationException(
                        $"DRAFT_PRESENT: editor contém {draft.Length} caracteres; nenhuma substituição será feita.");
                EnsureSameTab(chat, pinned);
                if (chat.IsBusy())
                    throw new InvalidOperationException("RACE_BUSY_AGAIN: geração recomeçou antes do envio.");

                var text = $"Teste automatizado do chat-automation ({i}/3). Responda somente OK-{i}.";
                var startSend = Stopwatch.GetTimestamp();
                // Nunca repetir esta chamada se ocorrer erro depois do clique:
                // a entrega ao servidor pode ter acontecido.
                var receipt = chat.SendMessage(text);
                var sendFinished = Stopwatch.GetTimestamp();
                var status = JsonSerializer.SerializeToElement(receipt)
                    .GetProperty("status").GetString();
                Print(new
                {
                    status = "SEND_ATTEMPT_FINISHED",
                    sequence = i,
                    chat = chat.Name,
                    dispatchStatus = status,
                    sendCallMs = Math.Round(Stopwatch.GetElapsedTime(startSend, sendFinished).TotalMilliseconds, 2),
                    firstFalseToDispatchStartMs = Math.Round(
                        Stopwatch.GetElapsedTime(idle.FirstIdleTick, startSend).TotalMilliseconds, 2),
                    stableIdleToDispatchStartMs = Math.Round(
                        Stopwatch.GetElapsedTime(idle.ReadyTick, startSend).TotalMilliseconds, 2),
                    note = "Clique no botão não comprova entrega remota."
                });
                if (status != "INPUT_DISPATCHED_UNCONFIRMED")
                    throw new InvalidOperationException("SEND_STATUS_UNEXPECTED: sem retry automático.");

                // Para cada mensagem, exigir que a UIA tenha observado uma fase
                // busy=true e, em seguida, um período contínuo busy=false.
                idle = WaitForIdle(chat, options, requireBusy: true);
                Print(new
                {
                    status = "ANSWER_IDLE_OBSERVED",
                    sequence = i,
                    chat = chat.Name,
                    sawBusyTrue = idle.SawBusy,
                    readCount = idle.Samples,
                    falseToStableMs = Math.Round(
                        Stopwatch.GetElapsedTime(idle.FirstIdleTick, idle.ReadyTick).TotalMilliseconds, 2),
                    note = "Ociosidade UIA observada, não confirmação do conteúdo da resposta."
                });
            }
            Print(new { status = "COMPLETE_THREE_SEND_ATTEMPTS", chat = chat.Name,
                messagesAttempted = 3, responsesObservedIdle = 3 });
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    private static void EnsureSameTab(Chat chat, TabMatch expected)
    {
        var actual = chat.SetFocus();
        if (actual.Handle != expected.Handle ||
            !string.Equals(actual.TabRuntimeId, expected.TabRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(actual.Url, expected.Url, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "TARGET_TAB_CHANGED: o foco não corresponde à guia inicialmente fixada. Envio interrompido.");
    }

    private static IdleResult WaitForIdle(Chat chat, LabOptions options, bool requireBusy)
    {
        var readiness = new StableIdleGate(options.StableIdleMs, requireBusy);
        var start = Stopwatch.GetTimestamp();
        var lastProgress = start;
        var count = 0;
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            // Erros UIA abortam o teste, em vez de serem interpretados como false.
            var busy = chat.IsBusy();
            count++;
            var observed = Stopwatch.GetTimestamp();
            if (readiness.Observe(busy, observed))
                return new IdleResult(readiness.FirstIdleTick!.Value, observed,
                    readiness.SawBusy, count);

            if (requireBusy && !readiness.SawBusy &&
                Stopwatch.GetElapsedTime(start, observed).TotalSeconds > options.AckTimeoutSeconds)
                throw new InvalidOperationException(
                    "BUSY_NEVER_OBSERVED: após o envio, nenhuma fase true foi detectada. " +
                    "Sequência interrompida; a mensagem NÃO será reenviada.");
            if (requireBusy && readiness.FirstBusyTick is { } firstBusy &&
                Stopwatch.GetElapsedTime(firstBusy, observed).TotalSeconds > options.ResponseTimeoutSeconds)
                throw new InvalidOperationException(
                    "RESPONSE_TIMEOUT: busy permaneceu ativo/instável além do limite.");
            if (!requireBusy &&
                Stopwatch.GetElapsedTime(start, observed).TotalSeconds > options.ResponseTimeoutSeconds)
                throw new InvalidOperationException("INITIAL_IDLE_TIMEOUT: conversa não ficou ociosa.");

            if (Stopwatch.GetElapsedTime(lastProgress, observed).TotalSeconds >= 5)
            {
                Print(new
                {
                    status = "WAITING",
                    chat = chat.Name,
                    phase = requireBusy && !readiness.SawBusy
                        ? "AWAIT_BUSY_TRUE"
                        : "AWAIT_STABLE_FALSE",
                    lastBusy = busy,
                    samples = count,
                    elapsedSeconds = Math.Round(Stopwatch.GetElapsedTime(start, observed).TotalSeconds, 1)
                });
                lastProgress = observed;
            }
            SleepToMinimumInterval(started, options.IntervalMs);
        }
    }

    private static void SleepToMinimumInterval(long probeStartedTick, int intervalMs)
    {
        // Evita busy-spin caso a própria consulta dure mais que o intervalo.
        var remaining = intervalMs - Stopwatch.GetElapsedTime(probeStartedTick).TotalMilliseconds;
        Thread.Sleep(remaining > 1 ? (int)Math.Ceiling(remaining) : 1);
    }

    private static Dictionary<int, double> EdgeCpu()
    {
        var result = new Dictionary<int, double>();
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            using (process)
            {
                try { result[process.Id] = process.TotalProcessorTime.TotalMilliseconds; }
                catch (InvalidOperationException) { /* Processo encerrado durante a medição. */ }
                catch (System.ComponentModel.Win32Exception) { /* Sem permissão para medição. */ }
            }
        }
        return result;
    }

    private static object Statistics(List<double> values)
    {
        if (values.Count == 0) return new { count = 0 };
        var ordered = values.OrderBy(x => x).ToArray();
        double P(double quantile) => Math.Round(ordered[(int)Math.Ceiling((ordered.Length - 1) * quantile)], 2);
        return new { count = values.Count, min = P(0), p50 = P(.5), p95 = P(.95),
            p99 = P(.99), max = P(1), mean = Math.Round(values.Average(), 2) };
    }

    private static void SelfTest()
    {
        long Tick(int ms) => (long)Math.Round(ms / 1000.0 * Stopwatch.Frequency);
        void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("SELF_TEST_FAILED");
        }
        Require(LabOptions.Parse(Array.Empty<string>()).StableIdleMs == 300);
        var first = new StableIdleGate(250, requireBusy: false);
        Require(!first.Observe(false, Tick(0)));
        Require(!first.Observe(false, Tick(249)));
        Require(first.Observe(false, Tick(250)));
        var follow = new StableIdleGate(250, requireBusy: true);
        Require(!follow.Observe(false, Tick(0))); // false logo após clique NÃO é ack.
        Require(!follow.Observe(false, Tick(500)));
        Require(!follow.Observe(true, Tick(510)));
        Require(!follow.Observe(false, Tick(520)));
        Require(!follow.Observe(false, Tick(700)));
        Require(!follow.Observe(true, Tick(710))); // true reinicia a estabilidade.
        Require(!follow.Observe(false, Tick(720)));
        Require(follow.Observe(false, Tick(970)));
        Require(follow.SawBusy);
    }

    private static void Print(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}

internal sealed class StableIdleGate(int stableMs, bool requireBusy)
{
    public bool SawBusy { get; private set; }
    public long? FirstBusyTick { get; private set; }
    public long? FirstIdleTick { get; private set; }

    public bool Observe(bool busy, long tick)
    {
        if (busy)
        {
            SawBusy = true;
            FirstBusyTick ??= tick;
            FirstIdleTick = null;
            return false;
        }
        if (requireBusy && !SawBusy) return false;
        FirstIdleTick ??= tick;
        return Stopwatch.GetElapsedTime(FirstIdleTick.Value, tick).TotalMilliseconds >= stableMs;
    }
}

internal sealed record IdleResult(long FirstIdleTick, long ReadyTick, bool SawBusy, int Samples);

internal sealed class LabOptions
{
    public string Mode { get; init; } = "probe";
    public string Chat { get; init; } = "chat00";
    public int IntervalMs { get; init; } = 20;
    public int DurationSeconds { get; init; } = 15;
    public int StableIdleMs { get; init; } = 300;
    public int AckTimeoutSeconds { get; init; } = 30;
    public int ResponseTimeoutSeconds { get; init; } = 180;
    public bool AllowSend { get; init; }

    public static LabOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool allowSend = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--allow-send", StringComparison.OrdinalIgnoreCase))
            {
                allowSend = true;
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length)
                throw new ArgumentException("Opção inválida: " + args[i]);
            var name = args[i][2..];
            if (!new[] { "mode", "chat", "interval-ms", "duration-sec", "stable-idle-ms",
                         "ack-timeout-sec", "response-timeout-sec" }.Contains(name,
                         StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Opção desconhecida: " + args[i]);
            if (!map.TryAdd(name, args[++i]))
                throw new ArgumentException("Opção duplicada: --" + name);
        }
        string Value(string name, string fallback) => map.GetValueOrDefault(name, fallback);
        int Number(string name, int fallback, int min, int max)
        {
            var value = Value(name, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
                throw new ArgumentException($"--{name} deve estar entre {min} e {max}.");
            return parsed;
        }

        var mode = Value("mode", "probe").ToLowerInvariant();
        if (mode is not ("probe" or "send-three" or "self-test"))
            throw new ArgumentException("Use --mode probe, send-three ou self-test.");
        var chat = Value("chat", "chat00");
        if (string.IsNullOrWhiteSpace(chat))
            throw new ArgumentException("Informe --chat com o nome registrado.");
        return new LabOptions
        {
            Mode = mode,
            Chat = chat,
            IntervalMs = Number("interval-ms", 20, 20, 5000),
            DurationSeconds = Number("duration-sec", 15, 3, 120),
            StableIdleMs = Number("stable-idle-ms", 300, 100, 10000),
            AckTimeoutSeconds = Number("ack-timeout-sec", 30, 5, 90),
            ResponseTimeoutSeconds = Number("response-timeout-sec", 180, 10, 600),
            AllowSend = allowSend
        };
    }
}
