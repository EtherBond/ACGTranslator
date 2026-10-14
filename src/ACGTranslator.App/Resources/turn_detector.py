"""Ephemeral, local turn-boundary verification. No speaker identities or audio files.

Thresholds are conservative initial settings, NOT calibrated probabilities.
Only examine word boundaries, including boundaries without an audible pause.
"""
import time
import numpy as np

SAMPLE_RATE = 16000
MIN_SIDE = 0.8
MAX_CANDIDATES = 6


class SpeakerEmbedding:
    def __init__(self, path):
        import onnxruntime as ort
        import kaldi_native_fbank as knf
        self.knf = knf
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        self.session = ort.InferenceSession(str(path), sess_options=options,
                                           providers=["CPUExecutionProvider"])
        self.input_name = self.session.get_inputs()[0].name

    def __call__(self, audio):
        opts = self.knf.FbankOptions()
        opts.frame_opts.samp_freq = SAMPLE_RATE
        opts.frame_opts.dither = 0
        opts.frame_opts.window_type = "hamming"
        opts.frame_opts.snip_edges = True
        opts.mel_opts.num_bins = 80
        opts.mel_opts.low_freq = 20
        opts.mel_opts.high_freq = 0
        opts.energy_floor = 0
        bank = self.knf.OnlineFbank(opts)
        # The official WeSpeaker model uses unnormalised signed 16-bit PCM.
        bank.accept_waveform(SAMPLE_RATE, (np.asarray(audio) * 32768).tolist())
        bank.input_finished()
        features = np.stack([bank.get_frame(i) for i in range(bank.num_frames_ready)])
        features -= features.mean(axis=0, keepdims=True)
        vector = self.session.run(None, {self.input_name: features[None].astype(np.float32)})[0].reshape(-1)
        norm = np.linalg.norm(vector)
        if not np.isfinite(vector).all() or norm < 1e-8:
            raise ValueError("invalid speaker embedding")
        return vector / norm


def candidates(words):
    """Rank short gaps first; retain spaced no-gap boundaries for fast replies."""
    if len(words) < 2:
        return []
    start, end = words[0]["start"], words[-1]["end"]
    choices = []
    for left, right in zip(words, words[1:]):
        gap = right["start"] - left["end"]
        if gap < -0.04:
            continue  # Bad alignment/overlap is not evidence of a clean turn.
        point = (left["end"] + right["start"]) / 2
        if point - start >= MIN_SIDE and end - point >= MIN_SIDE:
            choices.append((min(max(gap, 0), .4), point))
    if not choices:
        return []
    # Cover the full segment instead of spending every candidate on its start.
    # Within each time bin prefer a pause, then the point nearest the bin centre.
    low, high = start + MIN_SIDE, end - MIN_SIDE
    bins = [[] for _ in range(MAX_CANDIDATES)]
    for gap, point in choices:
        index = min(MAX_CANDIDATES - 1, int((point-low) / max(high-low, .001) * MAX_CANDIDATES))
        bins[index].append((gap, point))
    selected = []
    for index, options in enumerate(bins):
        if options:
            centre = low + (index + .5) * (high-low) / MAX_CANDIDATES
            selected.append(min(options, key=lambda p: (-p[0], abs(p[1]-centre)))[1])
    return selected


def speech_coverage(words, start, end):
    intervals = sorted((max(start, w["start"]), min(end, w["end"])) for w in words
                       if w["end"] > start and w["start"] < end)
    total, cursor = 0., start
    for a, b in intervals:
        total += max(0., b - max(a, cursor))
        cursor = max(cursor, b)
    return total / max(end - start, .001)


class TurnDetector:
    def __init__(self, embed, budget_seconds=.65, clock=time.monotonic):
        self.embed = embed
        self.budget_seconds = budget_seconds
        self.clock = clock

    def detect(self, audio, words):
        started = self.clock()
        def check_budget():
            if self.clock() - started > self.budget_seconds:
                raise TimeoutError("turn analysis budget exceeded")

        verified = []
        for point in candidates(words):
            check_budget()
            left_start = max(words[0]["start"], point - 1.6)
            right_end = min(words[-1]["end"], point + 1.6)
            left_voiced = speech_coverage(words, left_start, point) * (point-left_start)
            right_voiced = speech_coverage(words, point, right_end) * (right_end-point)
            if min(left_voiced, right_voiced) < .6:
                continue
            def embedding(a, b):
                check_budget()
                value = self.embed(audio[round(a*SAMPLE_RATE):round(b*SAMPLE_RATE)])
                check_budget()
                return value
            left = embedding(left_start, point)
            right = embedding(point, right_end)
            similarity = float(left @ right)
            if similarity >= .35:
                continue
            # Check that the dissimilarity persists when removing boundary-adjacent
            # audio; rejects a transient/shout straddling the proposed boundary.
            inset = .12
            l2 = embedding(left_start, point - inset)
            r2 = embedding(point + inset, right_end)
            if min(float(left @ l2), float(right @ r2)) >= .65 and float(l2 @ r2) < .35:
                verified.append((max(similarity, float(l2 @ r2)), point))
        # The largest pause can be inside a reply. Choose the strongest verified
        # change, not the first candidate that happens to cross a threshold.
        return min(verified)[1] if verified else None
