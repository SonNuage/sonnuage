using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using SonNuage.Server.Data;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Analyse par le script Python d'Essentia (<c>Analysis/essentia_analyzer.py</c>), lancé une fois et gardé en vie
/// tant qu'il a du travail : ses modèles ne sont chargés qu'au démarrage. Une demande à la fois. Le processus s'arrête
/// après <see cref="AnalysisOptions.IdleMinutes"/> sans fichier, ou est relancé quand un fichier le bloque ou le tue.
/// </summary>
public sealed class EssentiaAudioAnalyzer : IAudioAnalyzer, IDisposable
{
    private const string BundledScript = "Analysis/essentia_analyzer.py";
    /// <summary>Plantages d'affilée au-delà desquels l'analyseur est jugé hors d'usage (et plus aucun morceau marqué en échec).</summary>
    private const int MaxConsecutiveCrashes = 3;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    private readonly AnalysisOptions _options;
    private readonly string _script;
    private readonly ILogger<EssentiaAudioAnalyzer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private readonly Lazy<bool> _available;
    private Process? _process;
    private int _crashes;

    public EssentiaAudioAnalyzer(IOptions<SonNuageOptions> options, ILogger<EssentiaAudioAnalyzer> logger)
    {
        _options = options.Value.Analysis;
        _script = string.IsNullOrWhiteSpace(_options.ScriptPath) ? Path.Combine(AppContext.BaseDirectory, BundledScript) : _options.ScriptPath;
        _logger = logger;
        _idle = new Timer(_ => StopWhenIdle());
        _available = new Lazy<bool>(() =>
        {
            var ok = File.Exists(_options.PythonPath) && Directory.Exists(_options.ModelsPath) && File.Exists(_script);
            if (ok) logger.LogInformation("Analyse audio disponible : {Python} {Script}", _options.PythonPath, _script);
            else logger.LogInformation("Analyse audio indisponible (Python, modèles ou script absents)");
            return ok;
        });
    }

    public bool Available => _available.Value;

    public async Task<AudioAnalysisOutcome> AnalyzeAsync(string path, CancellationToken ct)
    {
        if (!Available) throw new AudioAnalyzerUnavailableException("Essentia analyzer is not installed.");
        await _gate.WaitAsync(ct);
        try
        {
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            var process = _process ?? await StartAsync(ct);
            var id = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(EssentiaProtocol.Request(id, path).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (EssentiaProtocol.Parse(line) is not EssentiaProtocol.Result r || r.Id != id) continue;
                    _crashes = 0;
                    return r.Outcome;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Stop();
                return new AudioAnalysisOutcome.Failed($"Timeout: no result after {_options.TimeoutSeconds} s");
            }
            catch (OperationCanceledException)
            {
                // Tâche annulée : le processus est occupé par ce fichier, il repartira à neuf.
                Stop();
                throw;
            }

            var code = process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?";
            Stop();
            if (++_crashes >= MaxConsecutiveCrashes)
                throw new AudioAnalyzerUnavailableException($"Essentia analyzer keeps stopping (exit code {code}).");
            return new AudioAnalysisOutcome.Failed($"Analyzer stopped on this file (exit code {code})");
        }
        finally
        {
            if (_process is not null) _idle.Change(TimeSpan.FromMinutes(Math.Max(1, _options.IdleMinutes)), Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private async Task<Process> StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_options.PythonPath!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-I", _script, "--models", _options.ModelsPath!, "--threads", Arg(_options.Threads), "--nice", Arg(_options.Niceness) })
            psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new AudioAnalyzerUnavailableException("Essentia analyzer did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new AudioAnalyzerUnavailableException($"Essentia analyzer did not start: {ex.Message}");
        }
        // Journaux de TensorFlow et d'Essentia : lus en continu (un tuyau plein bloquerait le script).
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogDebug("Essentia : {Line}", e.Data);
        };
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                switch (EssentiaProtocol.Parse(line))
                {
                    case EssentiaProtocol.Ready { Version: TrackAudioFeatures.CurrentVersion }:
                        _logger.LogInformation("Analyse audio : modèles chargés (processus {Pid})", process.Id);
                        _process = process;
                        return process;
                    case EssentiaProtocol.Ready ready:
                        throw new AudioAnalyzerUnavailableException(
                            $"Essentia analyzer computes version {ready.Version}, the server expects {TrackAudioFeatures.CurrentVersion}.");
                    case EssentiaProtocol.Fatal fatal:
                        throw new AudioAnalyzerUnavailableException($"Essentia analyzer failed to start: {fatal.Error}");
                }
            }
            throw new AudioAnalyzerUnavailableException("Essentia analyzer exited while loading its models.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new AudioAnalyzerUnavailableException("Essentia analyzer took too long to load its models.");
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void StopWhenIdle()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            if (_process is not null) _logger.LogInformation("Analyse audio : processus arrêté (inactif)");
            Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Stop()
    {
        if (_process is null) return;
        Kill(_process);
        _process = null;
    }

    private static void Kill(Process process)
    {
        try
        {
            // Fin de l'entrée : le script sort de lui-même ; sinon, arrêt forcé.
            process.StandardInput.Close();
            if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
        }
        process.Dispose();
    }

    public void Dispose()
    {
        _idle.Dispose();
        Stop();
        _gate.Dispose();
    }
}
