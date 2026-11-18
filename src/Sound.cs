using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Cubeland;

// Original sounds, generated in code (the same recipes as the web version). Each material has its own character and
// several variations, played back with a slight random pitch change.
//  - soft materials (grass, leaves, gravel, sand, dirt) = many tiny crackles of filtered noise
//  - hard materials (stone, wood, glass) = short ringing tones, like a struck object
// Plus mob voices, water sounds and the piano music (day and night songs).
public partial class Sound : Node
{
	const int SR = 44100, VARIANTS = 4;
	const float GRASS_MIX = 0.3f;
	public static Sound I;
	public static readonly Dictionary<string, float> Levels = new() { ["master"] = 1, ["block"] = 1, ["player"] = 1, ["mob"] = 1, ["music"] = 1 };
	public static bool Muted;
	static readonly Dictionary<int, string> Material = new()
	{
		[1] = "grass", [2] = "dirt", [3] = "rock", [4] = "wood", [5] = "grass", [6] = "sand", [7] = "wood", [8] = "cobble", [9] = "rock", [10] = "glass", [11] = "stone",
		[12] = "brick", [13] = "brick", [14] = "brick", [15] = "brick", [60] = "wood", [61] = "wood", [63] = "dirt", [64] = "brick", [65] = "brick", [66] = "brick",
		[67] = "brick", [68] = "brick", [69] = "rock",
	};
	static Sound()
	{
		for (int k = 0; k < 8; k++) Material[Blocks.Furnace + k] = "brick";
		foreach (int b in Blocks.NewColourBlocks) Material[b] = "brick";
		foreach (int b in Blocks.Flowers) Material[b] = "grass";
		foreach (int b in Blocks.GrainStages) Material[b] = "grass";
		foreach (int b in Blocks.Soils) Material[b] = "dirt";
	}
	static string Mat(int id) => Blocks.IsTorch(id) ? "wood" : Material.TryGetValue(id, out var m) ? m : "stone";

	// ---- tiny sound-building toolkit (works on raw sample arrays) ----
	static Func<double> Rng(int seed)
	{
		uint a = (uint)seed;
		return () =>
		{
			a += 0x6D2B79F5;
			uint t = (a ^ (a >> 15)) * (1 | a);
			t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
			return (t ^ (t >> 14)) / 4294967296.0;
		};
	}
	static float[] Filter(float[] buf, string type, double freq, double q)
	{
		double w = 2 * Math.PI * Math.Min(freq, SR * 0.45) / SR, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q), b0, b1, b2;
		if (type == "lp") { b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = b0; }
		else if (type == "hp") { b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = b0; }
		else { b0 = al; b1 = 0; b2 = -al; }
		double a0 = 1 + al, a1 = -2 * cs, a2 = 1 - al, x1 = 0, x2 = 0, y1 = 0, y2 = 0;
		for (int i = 0; i < buf.Length; i++)
		{
			double x = buf[i], y = (b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
			x2 = x1; x1 = x; y2 = y1; y1 = y; buf[i] = (float)y;
		}
		return buf;
	}
	// many tiny noise crackles scattered over time (crunch, rustle, grit)
	static void Crackles(float[] o, Func<double> r, int count, double spread, double start, double len0, double len1, double f0, double f1, double q = 1.5, double amp = 1, double bunch = 1.5)
	{
		for (int k = 0; k < count; k++)
		{
			double p = Math.Pow(r(), bunch), t0 = start + p * spread;
			int n = Math.Max(8, (int)((len0 + r() * (len1 - len0)) * SR));
			var g = new float[n];
			for (int i = 0; i < n; i++) g[i] = (float)(r() * 2 - 1);
			Filter(g, "bp", f0 + r() * (f1 - f0), q);
			double a = amp * (0.25 + 0.75 * r()) * (1 - p * 0.75); int off = (int)(t0 * SR);
			for (int i = 0; i < n && off + i < o.Length; i++) o[off + i] += (float)(g[i] * Math.Sin(Math.PI * i / n) * a);
		}
	}
	// a single shaped burst of filtered noise (thump, swoosh, click)
	static void Burst(float[] o, Func<double> r, double attack, double decay, string type, double freq, double q = 0.7, double amp = 1, double start = 0)
	{
		int n = (int)((attack + decay * 6) * SR); var g = new float[n];
		for (int i = 0; i < n; i++) g[i] = (float)(r() * 2 - 1);
		Filter(g, type, freq, q);
		int off = (int)(start * SR);
		for (int i = 0; i < n && off + i < o.Length; i++)
		{
			double t = (double)i / SR, env = t < attack ? t / attack : Math.Exp(-(t - attack) / decay);
			o[off + i] += (float)(g[i] * env * amp);
		}
	}
	// a decaying ringing tone (the "body" of something hard being struck)
	static void Ring(float[] o, double freq, double decay, double amp, double start = 0)
	{
		int off = (int)(start * SR), n = (int)(decay * 7 * SR);
		for (int i = 0; i < n && off + i < o.Length; i++)
		{
			double t = (double)i / SR;
			o[off + i] += (float)(amp * Math.Exp(-t / decay) * Math.Sin(2 * Math.PI * freq * t) * Math.Min(1, i / (0.0006 * SR)));
		}
	}
	static float[] Finish(float[] o, double peak)
	{
		float m = 0; foreach (float v in o) m = Math.Max(m, Math.Abs(v));
		double s = m > 0 ? peak / m : 0; double fade = 0.006 * SR;
		for (int i = 0; i < o.Length; i++) o[i] = (float)(o[i] * s * Math.Min(1, (o.Length - 1 - i) / fade));
		return o;
	}
	static float[] Buf(double secs) => new float[(int)(SR * secs)];

	// ---- recipes: (dig = true for breaking/placing, false for footsteps) ----
	static float[] Recipe(string mat, bool dig, Func<double> r)
	{
		float[] o;
		switch (mat)
		{
			case "grass":
				if (!dig)
				{
					o = Buf(0.24);
					Burst(o, r, 0.012, 0.038, "bp", 3200 * (0.9 + r() * 0.2), 0.6, 1);
					Burst(o, r, 0.004, 0.025, "lp", 300, amp: 0.15);
					return Finish(o, 0.5);
				}
				{
					double w = GRASS_MIX; o = Buf(0.36);
					Burst(o, r, 0.02, 0.055, "bp", 3200, 0.6, 0.85 * (1 - w) + 0.25 * w);
					Crackles(o, r, (int)Math.Round(90 * (0.35 + 0.65 * w)), 0.2, 0, 0.003, 0.008, 900, 2800, 2);
					Crackles(o, r, (int)Math.Round(35 * (1 - 0.4 * w)), 0.2, 0.005, 0.002, 0.006, 2000, 5000, 1.5, 0.5);
					Burst(o, r, 0.004, 0.032, "lp", 350, amp: 0.35 + 0.4 * w);
					return Finish(o, 0.7);
				}
			case "leaves":
				o = Buf(dig ? 0.4 : 0.25);
				Crackles(o, r, dig ? 110 : 50, dig ? 0.3 : 0.17, 0, 0.004, 0.012, 3500, 9000, 0.8, 1, 1.1);
				Burst(o, r, 0.02, 0.05, "bp", 5000, 0.5, 0.25);
				return Finish(o, dig ? 0.6 : 0.35);
			case "gravel":
				o = Buf(dig ? 0.36 : 0.22);
				Crackles(o, r, dig ? 120 : 50, dig ? 0.26 : 0.13, 0, 0.004, 0.013, 600, 2200, 3);
				Burst(o, r, 0.003, 0.035, "lp", 300, amp: 0.7);
				return Finish(o, dig ? 0.95 : 0.7);
			case "sand":
				if (!dig) { o = Buf(0.22); Burst(o, r, 0.02, 0.045, "hp", 4500, amp: 1); Burst(o, r, 0.02, 0.04, "bp", 6500, 0.8, 0.5); return Finish(o, 0.4); }
				o = Buf(0.32);
				Burst(o, r, 0.002, 0.03, "lp", 220, amp: 0.6);
				Burst(o, r, 0.002, 0.012, "bp", 700, 0.8, 0.25);
				Burst(o, r, 0.005, 0.05, "hp", 4500, amp: 0.6);
				Crackles(o, r, 140, 0.12, 0.003, 0.0008, 0.002, 4500, 9500, 1, 0.5, 1.8);
				return Finish(o, 0.65);
			case "dirt":
				if (!dig)
				{
					o = Buf(0.22);
					Crackles(o, r, 25, 0.13, 0, 0.015, 0.03, 200, 750, 0.8, 0.6, 1.2);
					Burst(o, r, 0.01, 0.035, "bp", 550, 0.5, 0.5);
					Filter(o, "lp", 2200, 0.7);
					return Finish(o, 0.65);
				}
				o = Buf(0.32);
				Burst(o, r, 0.008, 0.05, "lp", 450, amp: 1);
				Burst(o, r, 0.012, 0.04, "bp", 600, 0.5, 0.45);
				Crackles(o, r, 18, 0.18, 0.01, 0.02, 0.04, 200, 600, 0.7, 0.4, 1.2);
				Filter(o, "lp", 1400, 0.7);
				return Finish(o, 0.9);
			case "stone":
				o = Buf(dig ? 0.3 : 0.14);
				Burst(o, r, 0.0005, 0.004, "hp", 1800, amp: 0.7);
				Crackles(o, r, dig ? 45 : 34, dig ? 0.07 : 0.06, 0, 0.0015, 0.005, 1800, 4800, 4, 1, 1.8);
				Burst(o, r, 0.001, 0.02, "bp", 1100 + r() * 300, 1.5, 0.55);
				if (dig) Crackles(o, r, 45, 0.2, 0.03, 0.003, 0.01, 900, 2600, 2.5, 0.5);
				return Finish(o, dig ? 0.9 : 0.6);
			case "rock":
				if (!dig)
				{
					o = Buf(0.25);
					double f0 = 110 + r() * 30;
					Burst(o, r, 0.0008, 0.005, "lp", 1200, amp: 0.3);
					Burst(o, r, 0.002, 0.035, "lp", 220, amp: 1);
					Burst(o, r, 0.002, 0.04, "bp", f0, 1.2, 0.6);
					Burst(o, r, 0.002, 0.02, "bp", 400, 0.8, 0.35);
					Crackles(o, r, 8, 0.03, 0, 0.004, 0.01, 200, 600, 1.5, 0.3);
					return Finish(o, 0.8);
				}
				o = Buf(0.3);
				Burst(o, r, 0.0004, 0.008, "bp", 600, 0.9, 1);
				Crackles(o, r, 55, 0.09, 0, 0.004, 0.011, 260, 850, 2.5, 1, 2.2);
				Crackles(o, r, 20, 0.18, 0.03, 0.005, 0.014, 160, 520, 2, 0.45);
				Burst(o, r, 0.001, 0.035, "lp", 240, amp: 0.55);
				Filter(o, "lp", 1600, 0.7);
				return Finish(o, 0.9);
			case "wood":
				{
					o = Buf(0.16);
					double f0 = 320 + r() * 100;
					Burst(o, r, 0.0005, 0.004, "lp", 3000, amp: 0.6);
					Burst(o, r, 0.001, 0.025, "bp", f0, 3.5, 1);
					Burst(o, r, 0.002, 0.02, "lp", 260, amp: 0.45);
					Crackles(o, r, 8, 0.02, 0, 0.002, 0.005, 800, 2000, 2, 0.3);
					return Finish(o, 0.6);
				}
			case "glass":
				{
					o = Buf(0.3);
					Burst(o, r, 0.0003, 0.002, "hp", 5000, amp: 0.5);
					double f0 = 2100 + r() * 1400;
					foreach (var (m, a, d) in new[] { (1, 1, 0.11), (1.47, 0.7, 0.08), (2.09, 0.5, 0.06), (2.75, 0.3, 0.04) }) Ring(o, f0 * m, d, a);
					return Finish(o, dig ? 0.5 : 0.3);
				}
			case "shatter":
				o = Buf(0.45);
				Burst(o, r, 0.0003, 0.008, "hp", 2500, amp: 1);
				Burst(o, r, 0.0005, 0.015, "bp", 1800, 0.8, 0.6);
				Crackles(o, r, 70, 0.06, 0, 0.0008, 0.003, 2500, 7000, 3, 1, 2.5);
				for (int i = 0; i < 8; i++) Ring(o, 1500 + r() * 4000, 0.004 + r() * 0.018, 0.3 + r() * 0.5, Math.Pow(r(), 2.5) * 0.08);
				Crackles(o, r, 35, 0.25, 0.04, 0.001, 0.004, 1500, 4500, 2.5, 0.45, 1.6);
				return Finish(o, 0.9);
			case "cobble": return dig ? DeepGrit(r, true) : Recipe("rock", false, r);
			case "brick": return dig ? PlaceRock(r) : Recipe("rock", false, r);
		}
		return Buf(0.1);
	}
	static readonly string[] Recipes = { "grass", "leaves", "gravel", "sand", "dirt", "stone", "rock", "wood", "glass", "shatter", "cobble", "brick" };
	// stone set on stone: deep dry clack + thump + gritty scrape
	static float[] PlaceRock(Func<double> r)
	{
		var o = Buf(0.22);
		Burst(o, r, 0.0003, 0.005, "bp", 600 + r() * 120, 1.2, 0.8);
		Burst(o, r, 0.0005, 0.016, "bp", 300 + r() * 60, 1.2, 0.9);
		Burst(o, r, 0.0015, 0.032, "lp", 170, amp: 0.95);
		Crackles(o, r, 18, 0.05, 0.006, 0.004, 0.011, 260, 900, 2, 0.22, 1.6);
		Filter(o, "lp", 1600, 0.7);
		return Finish(o, 0.8);
	}
	static float[] DeepGrit(Func<double> r, bool dig)
	{
		var o = Buf(dig ? 0.3 : 0.24);
		Burst(o, r, 0.0008, 0.005, "lp", 700, amp: 0.5);
		Burst(o, r, 0.002, 0.035, "lp", 150, amp: 1);
		Crackles(o, r, dig ? 50 : 40, dig ? 0.09 : 0.065, 0.005, 0.006, 0.014, 180, 750, 2, 0.6, 1.6);
		return Finish(o, dig ? 0.9 : 0.8);
	}
	static readonly Dictionary<string, Func<Func<double>, float[]>> PlaceRecipes = new()
	{ ["rock"] = PlaceRock, ["cobble"] = r => DeepGrit(r, false), ["brick"] = PlaceRock };

	// Grr Angry's growl: a low, rough "grrrr": a buzzy throat tone shaped by mouth-like filters, plus breathy noise, rattled
	static float[] Growl(Func<double> r)
	{
		double len = 0.8 + r() * 0.5; int n = (int)(SR * len);
		var src = new float[n]; var breath = new float[n];
		double f0 = 68 + r() * 22;
		for (double t = 0.005; t < len;)
		{
			double p = t / len, f = f0 * (1 + 0.18 * Math.Sin(Math.PI * Math.Min(1, p * 1.3))) * (0.93 + r() * 0.14);
			int o = (int)(t * SR), plen = (int)(SR / f);
			for (int i = 0; i < plen && o + i < n; i++) src[o + i] += (float)Math.Exp(-i / (plen * 0.18));
			t += 1 / f;
		}
		for (int i = 0; i < n; i++) breath[i] = (float)(r() * 2 - 1);
		var low = Filter((float[])src.Clone(), "lp", 220, 0.8);
		var mouth = Filter((float[])src.Clone(), "bp", 420, 1.8);
		var snarl = Filter((float[])src.Clone(), "bp", 1150, 3);
		Filter(breath, "bp", 700, 0.8);
		var outp = new float[n]; double rate = 26 + r() * 10, wob = 5 + r() * 4;
		for (int i = 0; i < n; i++)
		{
			double t = (double)i / SR;
			double rattle = 0.5 + 0.5 * Math.Abs(Math.Sin(2 * Math.PI * rate * t + 2 * Math.Sin(2 * Math.PI * wob * t)));
			double env = Math.Min(1, t / 0.12) * (t > len * 0.55 ? Math.Pow(1 - (t - len * 0.55) / (len * 0.45), 1.5) : 1);
			outp[i] = (float)(env * rattle * (low[i] + mouth[i] * 2.2 + snarl[i] * 1.2 + breath[i] * 0.3));
		}
		Filter(outp, "hp", 45, 0.7);
		return Finish(outp, 0.9);
	}
	// Dan The Chocolate Man's "hrr": a high, cat-like trilled chirp ("mrrp")
	static float[] Hrr(Func<double> r)
	{
		double len = 0.35 + r() * 0.25; int n = (int)(SR * len);
		var src = new float[n]; var breath = new float[n];
		double f0 = 330 + r() * 90, up = 0.25 + r() * 0.15;
		for (double t = 0.003; t < len;)
		{
			double p = t / len, f = f0 * (1 + up * Math.Sin(Math.PI * Math.Min(1, p * 1.6)) - 0.12 * p) * (0.98 + r() * 0.04);
			int o = (int)(t * SR), plen = (int)(SR / f);
			for (int i = 0; i < plen && o + i < n; i++) src[o + i] += (float)Math.Exp(-i / (plen * 0.22));
			t += 1 / f;
		}
		for (int i = 0; i < n; i++) breath[i] = (float)(r() * 2 - 1);
		var body = Filter((float[])src.Clone(), "bp", 900 + r() * 150, 1.6);
		var bright = Filter((float[])src.Clone(), "bp", 2500, 2.5);
		Filter(breath, "bp", 3000, 0.8);
		var outp = new float[n]; double rate = 22 + r() * 6;
		for (int i = 0; i < n; i++)
		{
			double t = (double)i / SR;
			double trill = 0.55 + 0.45 * Math.Abs(Math.Sin(Math.PI * rate * t));
			double env = Math.Min(1, t / 0.04) * (t > len * 0.6 ? Math.Pow(1 - (t - len * 0.6) / (len * 0.4), 1.4) : 1);
			outp[i] = (float)(env * trill * (body[i] * 1.6 + bright[i] * 0.7 + src[i] * 0.15 + breath[i] * 0.08));
		}
		Filter(outp, "hp", 180, 0.7);
		return Finish(outp, 0.8);
	}
	// ---- water sounds ----
	static void Bubble(float[] o, double start, double freq, double len, double amp)
	{
		int off = (int)(start * SR), n = (int)(len * SR); double ph = 0;
		for (int i = 0; i < n && off + i < o.Length; i++)
		{
			double p = (double)i / n;
			ph += 2 * Math.PI * freq * (1 + 1.6 * p * p) / SR;
			o[off + i] += (float)(Math.Sin(ph) * amp * Math.Min(1, p * 12) * Math.Pow(1 - p, 2));
		}
	}
	static float[] Water(string kind, Func<double> r)
	{
		float[] o;
		if (kind == "splash")
		{
			o = Buf(0.9);
			Burst(o, r, 0.004, 0.04, "bp", 650 + r() * 200, 0.9, 0.8);
			Burst(o, r, 0.015, 0.09, "lp", 900, amp: 0.35, start: 0.01);
			Burst(o, r, 0.006, 0.07, "lp", 150, amp: 1.1);
			for (int k = 0; k < 14; k++) Bubble(o, 0.04 + r() * 0.5, 200 + r() * 380, 0.035 + r() * 0.05, 0.3 + r() * 0.3);
			for (int k = 0; k < 7; k++) Bubble(o, 0.15 + r() * 0.6, 550 + r() * 450, 0.015 + r() * 0.02, 0.1 + r() * 0.12);
			Filter(o, "lp", 1600, 0.7); Filter(o, "hp", 60, 0.7);
			return Finish(o, 0.85);
		}
		if (kind == "swim")
		{
			o = Buf(0.5);
			Burst(o, r, 0.08, 0.07, "lp", 320 + r() * 80, amp: 1);
			Burst(o, r, 0.06, 0.05, "bp", 200 + r() * 60, 1.2, 0.6);
			for (int k = 0; k < 2; k++) Bubble(o, 0.1 + r() * 0.2, 180 + r() * 140, 0.05 + r() * 0.04, 0.45);
			Filter(o, "lp", 700, 0.7);
			return Finish(o, 0.7);
		}
		o = Buf(0.45);
		Bubble(o, 0, 170 + r() * 50, 0.11, 1);
		Burst(o, r, 0.03, 0.06, "bp", 1100 + r() * 300, 0.6, 0.45, 0.03);
		for (int k = 0; k < 4; k++) Bubble(o, 0.08 + r() * 0.2, 350 + r() * 400, 0.025 + r() * 0.03, 0.3);
		return Finish(o, 0.8);
	}

	static AudioStreamWav Wav(float[] d, float gain = 1)
	{
		var bytes = new byte[d.Length * 2];
		for (int i = 0; i < d.Length; i++) { short s = (short)Math.Clamp(d[i] * gain * 32767, -32768, 32767); bytes[i * 2] = (byte)s; bytes[i * 2 + 1] = (byte)(s >> 8); }
		return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = SR, Stereo = false, Data = bytes };
	}

	// ---- the bank: every sound built once (in the background, at startup) ----
	readonly Dictionary<string, AudioStreamWav[]> bank = new();
	volatile bool ready;
	readonly Random rnd = new();
	readonly Dictionary<string, int> lastPlayed = new();
	readonly List<AudioStreamPlayer> voices = new();
	int sfxBus, musicBus;
	AudioEffectLowPassFilter muffle; bool muffled;

	public override void _Ready()
	{
		I = this;
		ProcessMode = ProcessModeEnum.Always;
		// a bus for sound effects (muffled underwater) and one for the music
		sfxBus = AudioServer.BusCount; AudioServer.AddBus(); AudioServer.SetBusName(sfxBus, "Sfx"); AudioServer.SetBusSend(sfxBus, "Master");
		muffle = new AudioEffectLowPassFilter { CutoffHz = 20000 };
		AudioServer.AddBusEffect(sfxBus, muffle);
		AudioServer.SetBusVolumeDb(sfxBus, Mathf.LinearToDb(0.7f));
		musicBus = AudioServer.BusCount; AudioServer.AddBus(); AudioServer.SetBusName(musicBus, "Music"); AudioServer.SetBusSend(musicBus, "Master");
		for (int i = 0; i < 24; i++) { var p = new AudioStreamPlayer { Bus = "Sfx" }; AddChild(p); voices.Add(p); }
		Task.Run(BuildBank);
	}
	void BuildBank()
	{
		var raw = new Dictionary<string, float[][]>();
		void Add(string key, Func<int, float[]> make) { var l = new float[VARIANTS][]; for (int v = 0; v < VARIANTS; v++) l[v] = make(v); raw[key] = l; }
		Add("hrr:say", v => Hrr(Rng(36000 + v * 43)));
		string[] wk = { "splash", "swim", "place" };
		for (int ki = 0; ki < wk.Length; ki++) { int k = ki; Add("water:" + wk[k], v => Water(wk[k], Rng(41000 + k * 211 + v * 37))); }
		Add("growl:say", v => Growl(Rng(31000 + v * 29)));
		for (int mi = 0; mi < Recipes.Length; mi++)
		{
			int m = mi;
			Add(Recipes[m] + ":step", v => Recipe(Recipes[m], false, Rng(9173 + m * 101 + v * 17)));
			Add(Recipes[m] + ":dig", v => Recipe(Recipes[m], true, Rng(9173 + m * 101 + v * 17 + 5000)));
		}
		foreach (var (m, f) in PlaceRecipes) Add(m + ":place", v => f(Rng(11000 + v * 17)));
		lock (bank) foreach (var (k, l) in raw) bank[k] = l.Select(d => Wav(d)).ToArray();
		ready = true;
		BuildMusic();
	}
	AudioStream Pick(string key)
	{
		if (!ready || !bank.TryGetValue(key, out var list)) return null;
		int i = rnd.Next(list.Length);
		if (list.Length > 1 && lastPlayed.TryGetValue(key, out var last) && i == last) i = (i + 1) % list.Length;   // don't repeat the same variation twice
		lastPlayed[key] = i;
		return list[i];
	}
	// ch = which volume slider this sound follows: "block", "player" or "mob" (all of them also follow the master volume)
	void Play(string mat, string kind, float vol, float rate, string ch = "block")
	{
		vol *= Levels["master"] * Levels[ch];
		if (Muted || vol <= 0) return;
		var s = Pick(mat + ":" + kind); if (s == null) return;
		var p = voices.FirstOrDefault(v => !v.Playing) ?? voices[0];
		p.Stream = s; p.VolumeDb = Mathf.LinearToDb(vol); p.PitchScale = rate * (0.92f + (float)rnd.NextDouble() * 0.16f);
		p.Play();
	}
	// a sound from a spot in the world (mob voices): quieter the further away, from the left or right speaker
	public void PlayAt(string voice, Vector3 at, float vol)
	{
		vol *= Levels["master"] * Levels["mob"];
		if (Muted || vol <= 0.01f) return;
		var s = Pick(voice + ":say"); if (s == null) return;
		var p = new AudioStreamPlayer3D { Stream = s, Bus = "Sfx", AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, VolumeDb = Mathf.LinearToDb(vol),
			PitchScale = 0.92f + (float)rnd.NextDouble() * 0.16f, PanningStrength = 0.8f, Position = at };
		AddChild(p); p.Finished += p.QueueFree; p.Play();
	}

	public void Step(int id) => Play(Mat(id), "step", 0.45f, 1, "player");
	public void Jump(int id) => Play(Mat(id), "step", 0.3f, 1.05f, "player");
	public void Land(int id, float speed) => Play(Mat(id), "step", Math.Min(1, 0.4f + speed / 18), 0.85f, "player");
	public void Splash(float speed) => Play("water", "splash", Math.Min(1, 0.3f + speed / 14), speed > 12 ? 0.85f : 1, "player");
	public void Swim() => Play("water", "swim", 0.55f, 1, "player");
	public void Pour() => Play("water", "place", 0.8f, 1);
	public void Place(int id) { var m = Mat(id); if (PlaceRecipes.ContainsKey(m)) Play(m, "place", 0.9f, 1); else Play(m, "dig", 0.75f, 0.9f); }
	public void Break(int id)
	{
		var m = Mat(id);
		if (m == "glass") Play("shatter", "dig", 0.9f, 1);
		else Play(m, "dig", 1, m == "rock" || m == "cobble" || m == "brick" ? 1 : 0.8f);   // rock's "crk" stays medium-pitched
	}
	// head underwater: everything (except the music) sounds muffled
	public void Underwater(bool on) { if (muffled == on) return; muffled = on; muffleTarget = on ? 650 : 20000; }
	float muffleTarget = 20000;

	// ---------- Music: a synthesized piano ----------
	// Song notation: a space = one beat, notes written with no space between them = half a beat.
	// [ ... ] = notes played together. Each song loops nonstop and crossfades at sunrise / sunset.
	static string NightSong()
	{
		string bar1 = "[f2 g#2 c3 f4] g#4 c5 g#4 ", bar2 = "[e2 g2 c3 e4] g4 c5 g4 ";
		string phrase = string.Concat(Enumerable.Repeat(bar1, 4)) + string.Concat(Enumerable.Repeat(bar2, 3)) + "[e2 g2 c3 e4] g4 c5c#5c5b4";
		return string.Concat(Enumerable.Repeat(phrase, 4));
	}
	static string DaySong()
	{
		string F = "[f2a2c3f4]g4a4c5" + string.Concat(Enumerable.Repeat("f4g4a4c5", 3));
		string Bb = "[a#2d3f3A#4]c5d5f5" + string.Concat(Enumerable.Repeat("a#4c5d5f5", 3));
		return F + F + Bb + Bb;
	}
	const double BPM = 120;
	static readonly Dictionary<string, int> NoteIdx = new() { ["c"] = 0, ["c#"] = 1, ["d"] = 2, ["d#"] = 3, ["e"] = 4, ["f"] = 5, ["f#"] = 6, ["g"] = 7, ["g#"] = 8, ["a"] = 9, ["a#"] = 10, ["b"] = 11 };
	static (List<(double t, List<int> notes)> events, double length) ParseSong(string text)
	{
		double beat = 60 / BPM, t = 0; int i = 0; var events = new List<(double, List<int>)>();
		int? ReadNote()
		{
			var m = System.Text.RegularExpressions.Regex.Match(text[i..], "^([a-gA-G]#?)(\\d)");
			if (!m.Success) return null;
			i += m.Length;
			return 12 * (int.Parse(m.Groups[2].Value) + 1) + NoteIdx[m.Groups[1].Value.ToLower()];
		}
		while (i < text.Length)
		{
			var notes = new List<int>();
			if (text[i] == '[')
			{
				i++;
				while (i < text.Length && text[i] != ']') { if (text[i] == ' ') { i++; continue; } var n = ReadNote(); if (n == null) { i++; continue; } notes.Add(n.Value); }
				i++;
			}
			else { var n = ReadNote(); if (n == null) { i++; continue; } notes.Add(n.Value); }
			events.Add((t, notes));
			int spaces = 0; while (i < text.Length && text[i] == ' ') { spaces++; i++; }
			t += spaces > 0 ? spaces * beat : beat / 2;
		}
		return (events, t);
	}
	static readonly Dictionary<int, float[]> pianoCache = new();
	static float[] PianoNote(int midi)   // additive piano: slightly stretched partials, two detuned strings, hammer knock
	{
		if (pianoCache.TryGetValue(midi, out var have)) return have;
		double f0 = 440 * Math.Pow(2, (midi - 69) / 12.0); bool low = midi < 55;
		int len = (int)(SR * (low ? 3.2 : 2.4)); var d = new float[len];
		double baseDecay = 0.9 + Math.Max(0, midi - 40) * 0.035;
		for (int n = 1; n <= 10; n++)
		{
			double f = f0 * n * Math.Sqrt(1 + 0.0004 * n * n);
			if (f > SR / 2.2) break;
			double amp = (low ? 0.8 : 1) / Math.Pow(n, low ? 1.1 : 1.5) * (n == 1 ? 1 : 0.9), decay = baseDecay * (1 + (n - 1) * 0.45);
			foreach (double det in new[] { -0.0011, 0.0011 })
			{
				double w = 2 * Math.PI * f * (1 + det) / SR;
				for (int s = 0; s < len; s++) d[s] += (float)(Math.Sin(w * s) * amp * Math.Exp(-decay * s / SR) * 0.5);
			}
		}
		var r = Rng(20000 + midi); double lp = 0;
		for (int s = 0; s < SR * 0.03; s++) { lp += ((r() * 2 - 1) - lp) * 0.08; d[s] += (float)(lp * 0.5 * Math.Exp(-s / (SR * 0.006))); }
		float peak = 0;
		for (int s = 0; s < len; s++)
		{
			if (s < SR * 0.002) d[s] *= (float)(s / (SR * 0.002));
			if (s > len - SR * 0.3) d[s] *= (float)((len - s) / (SR * 0.3));
			peak = Math.Max(peak, Math.Abs(d[s]));
		}
		for (int s = 0; s < len; s++) d[s] /= peak;
		return pianoCache[midi] = d;
	}
	// one full pass of a song, rendered as a loop (the notes ringing past the end carry over into the start, so it loops without a seam)
	static (AudioStreamWav wav, float peak) RenderSong(string text)
	{
		var (events, length) = ParseSong(text);
		int n = (int)(length * SR); var o = new float[n];
		foreach (var (t, notes) in events)
		{
			bool chord = notes.Count > 1;
			foreach (int m in notes)
			{
				var p = PianoNote(m); float g = (chord ? 0.42f : 0.6f) * (m < 55 ? 0.9f : 1);
				int off = (int)(t * SR);
				for (int s = 0; s < p.Length; s++) o[(off + s) % n] += p[s] * g;
			}
		}
		float peak = o.Max(Math.Abs);
		var wav = Wav(o, 1 / peak);
		wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward; wav.LoopBegin = 0; wav.LoopEnd = n;
		return (wav, peak);
	}
	readonly Dictionary<string, (AudioStreamWav wav, float peak)> songs = new();
	void BuildMusic()
	{
		var day = RenderSong(DaySong()); var night = RenderSong(NightSong());
		lock (songs) { songs["day"] = day; songs["night"] = night; }
	}
	// the song playing now, and the one fading out
	AudioStreamPlayer current, fading; string currentName, wanted; double waitUntil;
	float MusicLevel => Muted ? 0 : 0.32f * Levels["master"] * Levels["music"];
	public void Music(string name) => wanted = name;   // "day" or "night" (called every frame)
	AudioStreamPlayer StartSong(string name, bool fadeIn)
	{
		(AudioStreamWav wav, float peak) s; lock (songs) if (!songs.TryGetValue(name, out s)) return null;
		var p = new AudioStreamPlayer { Stream = s.wav, Bus = "Music" }; p.SetMeta("peak", s.peak); p.SetMeta("fade", fadeIn ? 0f : 1f);
		AddChild(p); p.Play();
		return p;
	}
	public override void _Process(double delta)
	{
		float dt = (float)delta;
		// the underwater muffle closes and opens smoothly
		muffle.CutoffHz += (muffleTarget - muffle.CutoffHz) * Math.Min(1, dt * 20);
		AudioServer.SetBusVolumeDb(musicBus, Mathf.LinearToDb(Math.Max(1e-4f, MusicLevel)));
		if (wanted == null) return;
		double now = Time.GetTicksMsec() / 1000.0;
		if (current == null && currentName == null) { current = StartSong(wanted, false); if (current != null) currentName = wanted; }
		else if (currentName != wanted && current != null)
		{   // the old song fades out completely first; the new one waits, then fades in
			fading?.QueueFree(); fading = current; fading.SetMeta("out", 1f); current = null; currentName = wanted; waitUntil = now + 4.2;
		}
		else if (current == null && currentName != null && now >= waitUntil) current = StartSong(currentName, true);
		foreach (var p in new[] { current, fading })
		{
			if (p == null) continue;
			float peak = (float)p.GetMeta("peak");
			float f = (float)p.GetMeta("fade");
			f = p.HasMeta("out") ? f - dt / 4 : Math.Min(1, f + dt / 3);
			p.SetMeta("fade", f);
			p.VolumeDb = Mathf.LinearToDb(Math.Max(1e-4f, f * peak));
			if (p == fading && f <= 0) { fading.QueueFree(); fading = null; }
		}
	}
}
