using Microsoft.Maui.Graphics;
using AView = Android.Views.View;

namespace Microsoft.Maui.Platform
{
	public static class StrokeExtensions
	{
		public static void UpdateBorderStroke(this AView platformView, IBorderStroke border)
		{
			// Always set the drawable first
			MauiDrawable? mauiDrawable = null;
			if (platformView.UpdateRoundRectangleSolidDrawable(border))
				return;

			platformView.UpdateMauiDrawable(border, ref mauiDrawable);

			if (mauiDrawable is null)
				return;
			if (border.Shape is null)
				return;

			mauiDrawable.SetBorderBrush(border.Stroke);
			mauiDrawable.SetBorderWidth(border.StrokeThickness);
			platformView.UpdateStrokeDashPattern(border, mauiDrawable);
			platformView.UpdateStrokeDashOffset(border, mauiDrawable);
			mauiDrawable.SetBorderMiterLimit(border.StrokeMiterLimit);
			mauiDrawable.SetBorderLineCap(border.StrokeLineCap);
			mauiDrawable.SetBorderLineJoin(border.StrokeLineJoin);
		}

		public static void UpdateStrokeShape(this AView platformView, IBorderStroke border)
		{
			var borderShape = border.Shape;
			MauiDrawable? mauiDrawable = platformView.Background as MauiDrawable;

			if (mauiDrawable == null && borderShape == null)
			{
				platformView.ClearRoundRectangleSolidDrawable();
				if (platformView is ContentViewGroup contentViewGroup)
					contentViewGroup.ClearBackgroundOutlineClip();

				return;
			}

			platformView.UpdateMauiDrawable(border, ref mauiDrawable);
			// Make sure to invalidate the wrapper view so that the eventual shadow is redrawn
			(platformView.Parent as WrapperView)?.Invalidate();
		}

		public static void UpdateStroke(this AView platformView, IBorderStroke border)
		{
			var stroke = border.Stroke;
			MauiDrawable? mauiDrawable = platformView.Background as MauiDrawable;

			if (mauiDrawable == null && stroke.IsNullOrEmpty())
				return;

			platformView.UpdateMauiDrawable(border, ref mauiDrawable);
			mauiDrawable?.SetBorderBrush(border.Stroke);
		}

		public static void UpdateStrokeThickness(this AView platformView, IBorderStroke border)
		{
			MauiDrawable? mauiDrawable = platformView.Background as MauiDrawable;
			bool hasBorder = border.Shape != null && border.Stroke != null;

			if (mauiDrawable == null && !hasBorder)
				return;

			mauiDrawable?.SetBorderWidth(border.StrokeThickness);
		}

		public static void UpdateStrokeDashPattern(this AView platformView, IBorderStroke border) =>
			UpdateStrokeDashPattern(platformView, border, platformView.Background as MauiDrawable);

		internal static void UpdateStrokeDashPattern(this AView platformView, IBorderStroke border, MauiDrawable? mauiDrawable)
		{
			if (mauiDrawable is null)
				return;

			var strokeDashPattern = border.StrokeDashPattern;
			bool hasBorder = border.Shape != null && border.Stroke != null;
			if (!hasBorder && (strokeDashPattern == null || strokeDashPattern.Length == 0))
				return;

			mauiDrawable.SetBorderDash(strokeDashPattern, border.StrokeDashOffset);
		}

		public static void UpdateStrokeDashOffset(this AView platformView, IBorderStroke border) =>
			UpdateStrokeDashOffset(platformView, border, platformView.Background as MauiDrawable);

		internal static void UpdateStrokeDashOffset(this AView platformView, IBorderStroke border, MauiDrawable? mauiDrawable)
		{
			if (mauiDrawable is null)
				return;
			bool hasBorder = border.Shape != null && border.Stroke != null;
			if (!hasBorder)
				return;

			mauiDrawable.SetBorderDash(border.StrokeDashPattern, border.StrokeDashOffset);
		}

		public static void UpdateStrokeMiterLimit(this AView platformView, IBorderStroke border)
		{
			MauiDrawable? mauiDrawable = platformView.Background as MauiDrawable;

			bool hasBorder = border.Shape != null && border.Stroke != null;

			if (mauiDrawable == null && !hasBorder)
				return;

			mauiDrawable?.SetBorderMiterLimit(border.StrokeMiterLimit);
		}

		public static void UpdateStrokeLineCap(this AView platformView, IBorderStroke border)
		{
			MauiDrawable? mauiDrawable = platformView.Background as MauiDrawable;
			bool hasBorder = border.Shape != null && border.Stroke != null;

			if (mauiDrawable == null && !hasBorder)
				return;

			mauiDrawable?.SetBorderLineCap(border.StrokeLineCap);
		}

		public static void UpdateStrokeLineJoin(this AView platformView, IBorderStroke border)
		{
			MauiDrawable? mauiDrawable = platformView.Background as MauiDrawable;
			bool hasBorder = border.Shape != null && border.Stroke != null;

			if (mauiDrawable == null && !hasBorder)
				return;

			mauiDrawable?.SetBorderLineJoin(border.StrokeLineJoin);
		}

		public static void InvalidateBorderStrokeBounds(this AView platformView)
		{
			if (platformView.Background is not MauiDrawable mauiDrawable)
				return;

			mauiDrawable.InvalidateBorderBounds();
		}

		internal static void UpdateMauiDrawable(this AView platformView, IBorderStroke border, ref MauiDrawable? mauiDrawable)
		{
			bool hasBorder = border.Shape != null;

			if (!hasBorder)
				return;

			mauiDrawable ??= platformView.Background as MauiDrawable;
			if (mauiDrawable is null)
			{
				if (platformView.Background is MauiRoundRectangleSolidDrawable roundRectangleSolidDrawable)
				{
					platformView.Background = null;
					roundRectangleSolidDrawable.Dispose();
				}

				mauiDrawable = new MauiDrawable(platformView.Context);

				platformView.Background = mauiDrawable;
			}

			if (border is IView v)
				mauiDrawable.SetBackground(v.Background);
			else
				mauiDrawable.SetBackground(new SolidPaint(Colors.Transparent));

			mauiDrawable.SetBorderShape(border.Shape);

			if (platformView is ContentViewGroup contentViewGroup)
			{
				contentViewGroup.ClearBackgroundOutlineClip();
				contentViewGroup.Clip = border;
			}
		}

		static bool UpdateRoundRectangleSolidDrawable(this AView platformView, IBorderStroke border)
		{
			if (border is not IView view)
				return false;

			if (view.Shadow is not null)
				return false;

			if (border.Shape is not IRoundRectangle roundRectangle)
				return false;

			if (!border.Stroke.IsNullOrEmpty() && border.StrokeThickness > 0)
				return false;

			if (platformView is ContentViewGroup contentViewGroup)
			{
				if (roundRectangle.CornerRadius.IsUniform())
				{
					contentViewGroup.SetBackgroundOutlineClip();
				}
				else
				{
					contentViewGroup.ClearBackgroundOutlineClip();
					contentViewGroup.Clip = border;
				}
			}

			if (view.Background.IsNullOrEmpty())
			{
				platformView.ClearRoundRectangleSolidDrawable();
				return true;
			}

			if (view.Background is not SolidPaint solidPaint || solidPaint.Color is null)
				return false;

			if (platformView.Background is MauiDrawable mauiDrawable)
			{
				platformView.Background = null;
				mauiDrawable.Dispose();
			}

			if (platformView.Background is not MauiRoundRectangleSolidDrawable drawable)
			{
				drawable = new MauiRoundRectangleSolidDrawable();
				platformView.Background = drawable;
			}

			drawable.Update(solidPaint.Color.ToPlatform(), roundRectangle.CornerRadius, platformView.Context.GetDisplayDensity());
			return true;
		}

		static void ClearRoundRectangleSolidDrawable(this AView platformView)
		{
			if (platformView.Background is MauiDrawable mauiDrawable)
			{
				platformView.Background = null;
				mauiDrawable.Dispose();
			}
			else if (platformView.Background is MauiRoundRectangleSolidDrawable drawable)
			{
				platformView.Background = null;
				drawable.Dispose();
			}
		}

		static bool IsUniform(this CornerRadius cornerRadius) =>
			cornerRadius.TopLeft == cornerRadius.TopRight &&
			cornerRadius.TopLeft == cornerRadius.BottomLeft &&
			cornerRadius.TopLeft == cornerRadius.BottomRight;
	}
}
