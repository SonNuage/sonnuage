"""Analyse audio de SonNuage (Essentia + modèles TensorFlow).

Processus de longue durée piloté par le serveur (EssentiaAudioAnalyzer) : les modèles sont chargés une fois, puis
chaque ligne JSON lue sur l'entrée standard ({"id": "...", "path": "..."}) donne une ligne JSON sur la sortie
standard : {"id": "...", "features": {...}, "seconds": 4.2} ou {"id": "...", "error": "..."}.
Au démarrage, une ligne {"ready": true, "version": 1} (ou {"fatal": "..."} et sortie en erreur).

Modèles Essentia (https://essentia.upf.edu/models/, licence CC BY-NC-SA 4.0) :
- discogs-effnet-bs64-1 (plongements) et ses têtes de classification (humeurs, dansabilité, voix/instrumental) ;
- msd-musicnn-1 (plongements) et emomusic-msd-musicnn-2 (valence et éveil, de 1 à 9, ramenés à 0..1) ;
- deeptemp-k16-3 (TempoCNN) pour le tempo ;
- genre_discogs400-discogs-effnet-1 : 400 styles Discogs rangés sous leur grand genre (« Rock---Pop Rock »), activations
  indépendantes de 0 à 1. Les libellés viennent du fichier .json du modèle. Les plus probables sont renvoyés dans
  « styles » : [["Rock---Pop Rock", 0.62], …].

Pour borner le temps de calcul, seules les trois minutes du milieu du morceau sont analysées (décodées par ffmpeg).
Usage : python -I essentia_analyzer.py --models /opt/analysis/models [--threads 2] [--nice 10] [--ffmpeg ffmpeg]
"""

import argparse
import json
import math
import os
import subprocess
import sys
import time

# Version du calcul : à monter avec TrackAudioFeatures.CurrentVersion quand les modèles ou le calcul changent.
VERSION = 2
SAMPLE_RATE = 16000
TEMPO_SAMPLE_RATE = 11025
MAX_SECONDS = 180.0
MIN_SECONDS = 3.0

EFFNET = "discogs-effnet-bs64-1.pb"
MUSICNN = "msd-musicnn-1.pb"
AROUSAL_VALENCE = "emomusic-msd-musicnn-2.pb"
TEMPO = "deeptemp-k16-3.pb"
GENRE = "genre_discogs400-discogs-effnet-1.pb"
GENRE_LABELS = "genre_discogs400-discogs-effnet-1.json"
TOP_STYLES = 5

# Têtes posées sur les plongements discogs-effnet : (caractéristique, fichier, rang de la classe positive).
# Les rangs viennent des métadonnées .json de chaque modèle (classes : ["happy", "non_happy"], ["non_party", "party"]…).
HEADS = [
    ("happy", "mood_happy-discogs-effnet-1.pb", 0),
    ("sad", "mood_sad-discogs-effnet-1.pb", 1),
    ("relaxed", "mood_relaxed-discogs-effnet-1.pb", 1),
    ("aggressive", "mood_aggressive-discogs-effnet-1.pb", 0),
    ("party", "mood_party-discogs-effnet-1.pb", 1),
    ("acoustic", "mood_acoustic-discogs-effnet-1.pb", 0),
    ("electronic", "mood_electronic-discogs-effnet-1.pb", 0),
    ("danceability", "danceability-discogs-effnet-1.pb", 0),
    ("instrumental", "voice_instrumental-discogs-effnet-1.pb", 0),
]


def protocol_channel():
    """Sortie réservée au protocole. Ce que TensorFlow ou Essentia écrivent sur la sortie standard part sur stderr."""
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    channel = os.fdopen(os.dup(1), "w", encoding="utf-8", buffering=1)
    os.dup2(2, 1)
    return channel


def send(channel, message):
    channel.write(json.dumps(message, ensure_ascii=False) + "\n")
    channel.flush()


def limit_resources(threads, niceness):
    for name in ("TF_NUM_INTRAOP_THREADS", "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
        os.environ[name] = str(threads)
    os.environ["TF_NUM_INTEROP_THREADS"] = "1"
    os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")
    if niceness > 0 and hasattr(os, "nice"):
        try:
            os.nice(niceness)
        except OSError:
            pass


def to_unit(value):
    """Valence et éveil emoMusic (échelle 1 à 9) ramenés à 0..1."""
    return min(1.0, max(0.0, (value - 1.0) / 8.0))


def rounded(value):
    return None if value is None or not math.isfinite(value) else round(float(value), 4)


def probe_duration(ffprobe, file):
    """Durée en secondes, ou None si ffprobe ne la donne pas."""
    out = subprocess.run([ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file],
                         capture_output=True, text=True, timeout=60, check=False)
    try:
        return float(out.stdout.strip())
    except ValueError:
        return None


def decode(ffmpeg, file, start, seconds):
    """Audio de [start, start + seconds], mono, 16 kHz, en flottants 32 bits."""
    out = subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-ss", f"{start:.3f}", "-t", f"{seconds:.3f}", "-i", file,
                          "-map", "0:a:0", "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "f32le", "-"],
                         capture_output=True, timeout=300, check=False)
    if out.returncode != 0:
        message = out.stderr.decode("utf-8", "replace").strip().splitlines()
        raise RuntimeError(f"ffmpeg: {message[-1] if message else 'exit code ' + str(out.returncode)}")
    import numpy
    return numpy.frombuffer(out.stdout, dtype=numpy.float32).copy()


class Analyzer:
    def __init__(self, models, ffmpeg, ffprobe):
        self.ffmpeg = ffmpeg
        self.ffprobe = ffprobe
        # Importé ici : les limites de fils doivent être posées avant le chargement de TensorFlow.
        import essentia
        import essentia.standard as es

        essentia.log.infoActive = False
        essentia.log.warningActive = False
        self.es = es

        def path(name):
            full = os.path.join(models, name)
            if not os.path.isfile(full):
                raise FileNotFoundError(f"model missing: {full}")
            return full

        self.effnet = es.TensorflowPredictEffnetDiscogs(graphFilename=path(EFFNET), output="PartitionedCall:1")
        self.heads = [(name, es.TensorflowPredict2D(graphFilename=path(file), output="model/Softmax"), index)
                      for name, file, index in HEADS]
        self.musicnn = es.TensorflowPredictMusiCNN(graphFilename=path(MUSICNN), output="model/dense/BiasAdd")
        self.arousal_valence = es.TensorflowPredict2D(graphFilename=path(AROUSAL_VALENCE), output="model/Identity")
        self.tempo = es.TempoCNN(graphFilename=path(TEMPO))
        # Entrée et sortie nommées dans les métadonnées du modèle (sigmoïde, une activation par style).
        self.genre = es.TensorflowPredict2D(graphFilename=path(GENRE), input="serving_default_model_Placeholder",
                                            output="PartitionedCall:0")
        with open(path(GENRE_LABELS), encoding="utf-8") as labels:
            self.styles = json.load(labels)["classes"]
        self.resample = es.Resample(inputSampleRate=SAMPLE_RATE, outputSampleRate=TEMPO_SAMPLE_RATE)

    def load(self, file):
        """
        Les trois minutes du milieu (tout le morceau s'il est plus court), en mono à 16 kHz, décodées par le ffmpeg du
        système : celui embarqué par Essentia ne lit pas l'Opus (imports YouTube) et peut planter sur certains fichiers.
        Seul l'extrait est décodé et gardé en mémoire, même pour un album entier d'une heure.
        """
        if not os.path.isfile(file):
            raise FileNotFoundError(f"no such file: {file}")
        duration = probe_duration(self.ffprobe, file)
        start = max(0.0, (duration - MAX_SECONDS) / 2) if duration else 0.0
        audio = decode(self.ffmpeg, file, start, MAX_SECONDS)
        if len(audio) < MIN_SECONDS * SAMPLE_RATE:
            raise ValueError(f"audio too short ({len(audio) / SAMPLE_RATE:.1f} s)")
        return audio

    def analyze(self, file):
        audio = self.load(file)
        features = {}
        embeddings = self.effnet(audio)
        for name, head, index in self.heads:
            features[name] = rounded(head(embeddings)[:, index].mean())
        activations = self.genre(embeddings).mean(axis=0)
        if len(activations) != len(self.styles):
            raise RuntimeError(f"genre model gives {len(activations)} values for {len(self.styles)} styles")
        best = activations.argsort()[::-1][:TOP_STYLES]
        features["styles"] = [[self.styles[i], rounded(activations[i])] for i in best]
        # Sorties emoMusic : (valence, éveil) par fenêtre.
        av = self.arousal_valence(self.musicnn(audio)).mean(axis=0)
        features["valence"] = rounded(to_unit(av[0]))
        features["energy"] = rounded(to_unit(av[1]))
        bpm = self.tempo(self.resample(audio))[0]
        features["bpm"] = rounded(bpm) if bpm and bpm > 0 else None
        return features


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--models", required=True)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--nice", type=int, default=10)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    args = parser.parse_args()

    channel = protocol_channel()
    limit_resources(max(1, args.threads), args.nice)
    try:
        analyzer = Analyzer(args.models, args.ffmpeg, args.ffprobe)
    except Exception as ex:  # modèles absents, Essentia introuvable : le serveur désactive l'analyse
        send(channel, {"fatal": f"{type(ex).__name__}: {ex}"})
        return 1
    send(channel, {"ready": True, "version": VERSION})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            started = time.monotonic()
            features = analyzer.analyze(request["path"])
            send(channel, {"id": request_id, "features": features, "seconds": round(time.monotonic() - started, 2)})
        except Exception as ex:  # un fichier illisible n'arrête pas le processus
            send(channel, {"id": request_id, "error": f"{type(ex).__name__}: {ex}"[:500]})
    return 0


if __name__ == "__main__":
    sys.exit(main())
