using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cubeland.Net;

// BinaryPack: the way PeerJS (what the web version uses for multiplayer) packs messages into bytes.
// A copy of peerjs-js-binarypack 2.1, so the Godot and web versions understand each other.
// Values: null, bool, long, double, string, byte[] (an ArrayBuffer), List<object>, Dictionary<string, object>.
public static class BinaryPack
{
	public static byte[] Pack(object value)
	{
		var w = new MemoryStream();
		Write(w, value);
		return w.ToArray();
	}
	static void U8(Stream w, int b) => w.WriteByte((byte)b);
	static void U16(Stream w, int n) { U8(w, n >> 8); U8(w, n & 0xff); }
	static void U32(Stream w, long n) { U8(w, (int)((n >> 24) & 0xff)); U8(w, (int)((n >> 16) & 0xff)); U8(w, (int)((n >> 8) & 0xff)); U8(w, (int)(n & 0xff)); }
	static void Write(Stream w, object v)
	{
		switch (v)
		{
			case null: U8(w, 0xc0); break;
			case bool b: U8(w, b ? 0xc3 : 0xc2); break;
			case string s: { var e = Encoding.UTF8.GetBytes(s); Len(w, e.Length, 0xb0, 0xd8, 0xd9); w.Write(e); break; }
			case byte[] bin: Len(w, bin.Length, 0xa0, 0xda, 0xdb); w.Write(bin); break;
			case int i: Int(w, i); break;
			case long l: Int(w, l); break;
			case byte by: Int(w, by); break;
			case float f: Num(w, f); break;
			case double d: Num(w, d); break;
			case System.Collections.IDictionary map:
				Len(w, map.Count, 0x80, 0xde, 0xdf);
				foreach (System.Collections.DictionaryEntry e in map) { Write(w, e.Key.ToString()); Write(w, e.Value); }
				break;
			case System.Collections.IEnumerable list:
			{
				var items = new List<object>(); foreach (var x in list) items.Add(x);
				Len(w, items.Count, 0x90, 0xdc, 0xdd);
				foreach (var x in items) Write(w, x);
				break;
			}
			default: throw new ArgumentException("BinaryPack can't pack " + v.GetType());
		}
	}
	static void Len(Stream w, int n, int small, int b16, int b32)
	{
		if (n <= 0x0f) U8(w, small + n);
		else if (n <= 0xffff) { U8(w, b16); U16(w, n); }
		else { U8(w, b32); U32(w, n); }
	}
	// numbers: whole ones as integers, others as doubles (like JavaScript, which has only one number type)
	static void Num(Stream w, double d)
	{
		if (Math.Floor(d) == d && Math.Abs(d) < 9e15) { Int(w, (long)d); return; }
		U8(w, 0xcb);
		long bits = BitConverter.DoubleToInt64Bits(d);
		for (int k = 7; k >= 0; k--) U8(w, (int)((bits >> (k * 8)) & 0xff));
	}
	static void Int(Stream w, long n)
	{
		if (n >= -32 && n <= 0x7f) U8(w, (int)(n & 0xff));
		else if (n >= 0 && n <= 0xff) { U8(w, 0xcc); U8(w, (int)n); }
		else if (n >= -128 && n <= 0x7f) { U8(w, 0xd0); U8(w, (int)(n & 0xff)); }
		else if (n >= 0 && n <= 0xffff) { U8(w, 0xcd); U16(w, (int)n); }
		else if (n >= -32768 && n <= 0x7fff) { U8(w, 0xd1); U16(w, (int)(n & 0xffff)); }
		else if (n >= 0 && n <= 0xffffffffL) { U8(w, 0xce); U32(w, n); }
		else if (n >= int.MinValue && n <= int.MaxValue) { U8(w, 0xd2); U32(w, n & 0xffffffffL); }
		else { U8(w, 0xd3); for (int k = 7; k >= 0; k--) U8(w, (int)((n >> (k * 8)) & 0xff)); }
	}

	public static object Unpack(byte[] data) { int i = 0; return Read(data, ref i); }
	static object Read(byte[] d, ref int i)
	{
		int type = d[i++];
		if (type < 0x80) return (long)type;
		if ((type ^ 0xe0) < 0x20) return (long)((type ^ 0xe0) - 0x20);
		int size;
		if ((size = type ^ 0xa0) <= 0x0f) return Raw(d, ref i, size);
		if ((size = type ^ 0xb0) <= 0x0f) return Str(d, ref i, size);
		if ((size = type ^ 0x90) <= 0x0f) return Arr(d, ref i, size);
		if ((size = type ^ 0x80) <= 0x0f) return Map(d, ref i, size);
		switch (type)
		{
			case 0xc0: case 0xc1: return null;
			case 0xc2: return false;
			case 0xc3: return true;
			case 0xca: { uint u = (uint)UInt(d, ref i, 4); return (double)BitConverter.Int32BitsToSingle((int)u); }
			case 0xcb: { long hi = UInt(d, ref i, 4), lo = UInt(d, ref i, 4); return BitConverter.Int64BitsToDouble((hi << 32) | lo); }
			case 0xcc: return UInt(d, ref i, 1);
			case 0xcd: return UInt(d, ref i, 2);
			case 0xce: return UInt(d, ref i, 4);
			case 0xcf: return UInt(d, ref i, 8);
			case 0xd0: return (long)(sbyte)UInt(d, ref i, 1);
			case 0xd1: return (long)(short)UInt(d, ref i, 2);
			case 0xd2: return (long)(int)UInt(d, ref i, 4);
			case 0xd3: return UInt(d, ref i, 8);
			case 0xd4: case 0xd5: case 0xd6: case 0xd7: return null;
			case 0xd8: return Str(d, ref i, (int)UInt(d, ref i, 2));
			case 0xd9: return Str(d, ref i, (int)UInt(d, ref i, 4));
			case 0xda: return Raw(d, ref i, (int)UInt(d, ref i, 2));
			case 0xdb: return Raw(d, ref i, (int)UInt(d, ref i, 4));
			case 0xdc: return Arr(d, ref i, (int)UInt(d, ref i, 2));
			case 0xdd: return Arr(d, ref i, (int)UInt(d, ref i, 4));
			case 0xde: return Map(d, ref i, (int)UInt(d, ref i, 2));
			case 0xdf: return Map(d, ref i, (int)UInt(d, ref i, 4));
		}
		return null;
	}
	static long UInt(byte[] d, ref int i, int n) { long v = 0; for (int k = 0; k < n; k++) v = (v << 8) | d[i++]; return v; }
	static byte[] Raw(byte[] d, ref int i, int n) { var b = new byte[n]; Array.Copy(d, i, b, 0, n); i += n; return b; }
	static string Str(byte[] d, ref int i, int n) { var s = Encoding.UTF8.GetString(d, i, n); i += n; return s; }
	static List<object> Arr(byte[] d, ref int i, int n) { var l = new List<object>(n); for (int k = 0; k < n; k++) l.Add(Read(d, ref i)); return l; }
	static Dictionary<string, object> Map(byte[] d, ref int i, int n)
	{
		var m = new Dictionary<string, object>(n);
		for (int k = 0; k < n; k++) { var key = Read(d, ref i); m[key?.ToString() ?? ""] = Read(d, ref i); }
		return m;
	}

	// reading values out of unpacked messages
	public static double Num(object o, double fallback = 0) => o switch { long l => l, double d => d, bool b => b ? 1 : 0, _ => fallback };
	public static float F(this Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? (float)Num(v) : 0;
	public static double Db(this Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? Num(v) : 0;   // (a whole double: the clock)
	public static int I(this Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? (int)Num(v) : 0;
	public static string S(this Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? v as string ?? v?.ToString() : null;
	public static List<object> L(this Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? v as List<object> : null;
	public static byte[] B(this Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? v as byte[] : null;
}
