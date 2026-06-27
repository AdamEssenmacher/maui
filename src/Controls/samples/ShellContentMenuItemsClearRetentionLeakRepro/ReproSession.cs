#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace ShellContentMenuItemsClearRetentionLeakRepro;

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<ShellContent> LiveShellContents = new();

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		LiveShellContents.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: remove ShellContent menu items individually",
			useClear: false);

		LiveShellContents.Clear();
		ForceFullGc();

		var current = await RunScenarioAsync(
			"current: ShellContent.MenuItems.Clear leaves parent hooks",
			useClear: true);

		ForceFullGc();
		GC.KeepAlive(LiveShellContents);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool useClear)
	{
		var shellContentRefs = new List<WeakReference<ShellContent>>(Attempts);
		var menuItemRefs = new List<WeakReference<MenuItem>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateAndRemoveMenuItem(
				useClear,
				shellContentRefs,
				menuItemRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var aliveShellContents = shellContentRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMenuItems = menuItemRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMenuItemsWithParent = menuItemRefs.Count(static wr => wr.TryGetTarget(out var menuItem) && menuItem.Parent is ShellContent);
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		GC.KeepAlive(LiveShellContents);

		return new RunStats(
			name,
			Attempts,
			aliveShellContents,
			aliveMenuItems,
			aliveMenuItemsWithParent,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateAndRemoveMenuItem(
		bool useClear,
		List<WeakReference<ShellContent>> shellContentRefs,
		List<WeakReference<MenuItem>> menuItemRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var menuItem = new MenuItem
		{
			Text = $"Invoice action {index}",
			BindingContext = payload
		};
		var shellContent = new ShellContent
		{
			Title = $"Customer {index}",
			Content = new ContentPage()
		};

		shellContent.MenuItems.Add(menuItem);

		LiveShellContents.Add(shellContent);
		shellContentRefs.Add(new WeakReference<ShellContent>(shellContent));
		menuItemRefs.Add(new WeakReference<MenuItem>(menuItem));
		payloadRefs.Add(new PayloadWeakReference(
			new WeakReference<Payload>(payload),
			new WeakReference<byte[]>(payload.Bytes)));

		if (useClear)
			shellContent.MenuItems.Clear();
		else
			shellContent.MenuItems.Remove(menuItem);

		menuItem = null!;
		shellContent = null!;
		payload = null!;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveShellContents,
	int AliveRemovedMenuItems,
	int AliveRemovedMenuItemsWithParent,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveShellContents == Attempts &&
		Control.AliveRemovedMenuItems == 0 &&
		Control.AliveRemovedMenuItemsWithParent == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveShellContents == Attempts &&
		Current.AliveRemovedMenuItems == Attempts &&
		Current.AliveRemovedMenuItemsWithParent == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("ShellContentMenuItemsClearRetentionLeakRepro");
		builder.AppendLine($"Attempts: {Attempts}");
		builder.AppendLine($"Payload per attempt: {FormatBytes(PayloadBytes)}");
		builder.AppendLine($"Leak proved: {LeakProved}");
		builder.AppendLine();
		AppendRun(builder, Control);
		builder.AppendLine();
		AppendRun(builder, Current);
		builder.AppendLine();
		builder.AppendLine($"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}");
		builder.AppendLine($"Managed heap final: {FormatBytes(ManagedHeapFinal)}");
		builder.AppendLine($"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
		return builder.ToString();
	}

	void AppendRun(StringBuilder builder, RunStats stats)
	{
		builder.AppendLine($"Run: {stats.Name}");
		builder.AppendLine($"  live ShellContents intentionally retained: {stats.AliveShellContents}/{stats.Attempts}");
		builder.AppendLine($"  removed menu items alive after full GC: {stats.AliveRemovedMenuItems}/{stats.Attempts}");
		builder.AppendLine($"  removed menu items still reporting ShellContent Parent: {stats.AliveRemovedMenuItemsWithParent}/{stats.Attempts}");
		builder.AppendLine($"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}");
		builder.AppendLine($"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}");
		builder.AppendLine($"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}
