using System;
using System.Threading.Tasks;
using Microsoft.Maui.DeviceTests.Stubs;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Primitives;
using ObjCRuntime;
using UIKit;
using Xunit;

namespace Microsoft.Maui.DeviceTests
{
	public partial class ProgressBarHandlerTests
	{
		UIProgressView GetNativeProgressBar(ProgressBarHandler progressBarHandler) =>
			progressBarHandler.PlatformView;

		double GetNativeProgress(ProgressBarHandler progressBarHandler) =>
			GetNativeProgressBar(progressBarHandler).Progress;

		[Theory(DisplayName = "Native ProgressBar Bounding Box Honors Explicit Size")]
		[InlineData(1)]
		[InlineData(100)]
		[InlineData(1000)]
		public async Task NativeProgressBarBoundingBoxHonorsExplicitSize(int size)
		{
			var progressBar = new ProgressBarStub
			{
				Height = size,
				Width = size,
				Progress = 0.5,
			};

			var nativeBoundingBox = await GetValueAsync(progressBar, handler => GetNativeProgressBar(handler).GetBoundingBox());

			AssertWithinTolerance(new Size(size, size), nativeBoundingBox.Size);
		}

		[Fact(DisplayName = "Native ProgressBar Does Not Scale To Implicit Arranged Height")]
		public async Task NativeProgressBarDoesNotScaleToImplicitArrangedHeight()
		{
			var progressBar = new ProgressBarStub
			{
				Height = Dimension.Unset,
				Width = 100,
				Progress = 0.5,
			};

			await InvokeOnMainThreadAsync(() =>
			{
				var handler = CreateHandler(progressBar);
				var arrangedState = ArrangeAndGetState(handler, new Rect(0, 0, 100, 200));

				AssertWithinTolerance(200, arrangedState.ContainerHeight);
				Assert.True(arrangedState.TransformIsIdentity);
			});
		}

		[Fact(DisplayName = "Native ProgressBar Updates Scaling When Explicit Height Changes")]
		public async Task NativeProgressBarUpdatesScalingWhenExplicitHeightChanges()
		{
			var progressBar = new ProgressBarStub
			{
				Height = Dimension.Unset,
				Width = 100,
				Progress = 0.5,
			};

			await InvokeOnMainThreadAsync(() =>
			{
				var handler = CreateHandler(progressBar);

				var arrangedState = ArrangeAndGetState(handler, new Rect(0, 0, 100, 200));
				Assert.True(arrangedState.TransformIsIdentity);

				progressBar.Height = 100;
				handler.UpdateValue(nameof(IView.Height));

				arrangedState = ArrangeAndGetState(handler, new Rect(0, 0, 100, 100));
				Assert.False(arrangedState.TransformIsIdentity);
				AssertWithinTolerance(new Size(100, 100), arrangedState.BoundingBoxSize);

				progressBar.Height = Dimension.Unset;
				handler.UpdateValue(nameof(IView.Height));

				arrangedState = ArrangeAndGetState(handler, new Rect(0, 0, 100, 200));
				Assert.True(arrangedState.TransformIsIdentity);
				AssertWithinTolerance(200, arrangedState.ContainerHeight);
			});
		}

		[Fact(DisplayName = "Native ProgressBar Does Not Scale To Constraint Height Without Explicit Height")]
		public async Task NativeProgressBarDoesNotScaleToConstraintHeightWithoutExplicitHeight()
		{
			var progressBar = new ProgressBarStub
			{
				Height = Dimension.Unset,
				MinimumHeight = 100,
				MaximumHeight = 1000,
				Width = 100,
				Progress = 0.5,
			};

			await InvokeOnMainThreadAsync(() =>
			{
				var handler = CreateHandler(progressBar);
				var arrangedState = ArrangeAndGetState(handler, new Rect(0, 0, 100, 200));

				AssertWithinTolerance(200, arrangedState.ContainerHeight);
				Assert.True(arrangedState.TransformIsIdentity);
			});
		}

		(bool TransformIsIdentity, double ContainerHeight, Size BoundingBoxSize) ArrangeAndGetState(
			ProgressBarHandler handler,
			Rect frame)
		{
			handler.PlatformArrange(frame);

			var containerView = handler.ContainerView;
			Assert.NotNull(containerView);
			containerView.LayoutSubviews();

			var nativeProgressBar = GetNativeProgressBar(handler);
			return (nativeProgressBar.Transform.IsIdentity, containerView.Bounds.Height, nativeProgressBar.GetBoundingBox().Size);
		}

		async Task ValidateNativeProgressColor(IProgress progressBar, Color color, Action action = null)
		{
			var expected = await GetValueAsync(progressBar, handler =>
			{
				var native = GetNativeProgressBar(handler);
				action?.Invoke();
				return native.ProgressTintColor.ToColor();
			});
			Assert.Equal(expected, color);
		}
	}
}
