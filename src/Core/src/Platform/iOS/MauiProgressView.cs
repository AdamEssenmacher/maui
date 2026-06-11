using System;
using CoreGraphics;
using UIKit;

namespace Microsoft.Maui.Platform
{
	internal sealed class MauiProgressView : UIProgressView
	{
		nfloat _arrangedHeight;
		bool _scalesToArrangedHeight;
		bool _isLayingOutSubviews;

		public MauiProgressView(UIProgressViewStyle style)
			: base(style)
		{
		}

		public bool ScalesToArrangedHeight
		{
			get => _scalesToArrangedHeight;
			set
			{
				if (_scalesToArrangedHeight == value)
				{
					return;
				}

				_scalesToArrangedHeight = value;

				if (_scalesToArrangedHeight)
				{
					LayoutToFrame(Frame);
				}
				else
				{
					ResetVerticalScale();
				}
			}
		}

		public override CGRect Frame
		{
			get => base.Frame;
			set
			{
				if (_isLayingOutSubviews)
				{
					base.Frame = value;
					return;
				}

				if (!ScalesToArrangedHeight)
				{
					LayoutNaturallyToFrame(value);
					return;
				}

				LayoutToFrame(value);
			}
		}

		public override CGRect Bounds
		{
			get => base.Bounds;
			set
			{
				if (_isLayingOutSubviews)
				{
					base.Bounds = value;
					return;
				}

				if (!ScalesToArrangedHeight)
				{
					LayoutNaturallyToBounds(value, Center);
					return;
				}

				LayoutToBounds(value, Center);
			}
		}

		public override void LayoutSubviews()
		{
			if (!ScalesToArrangedHeight)
			{
				LayoutSubviewsNaturally();
				return;
			}

			var arrangedHeight = _arrangedHeight;

			Transform = CGAffineTransform.MakeIdentity();

			_isLayingOutSubviews = true;
			try
			{
				base.LayoutSubviews();
			}
			finally
			{
				_isLayingOutSubviews = false;
				_arrangedHeight = arrangedHeight;
			}

			ApplyVerticalScale();
		}

		void LayoutSubviewsNaturally()
		{
			ResetVerticalScale();

			_isLayingOutSubviews = true;
			try
			{
				base.LayoutSubviews();
			}
			finally
			{
				_isLayingOutSubviews = false;
			}
		}

		void LayoutNaturallyToFrame(CGRect frame)
		{
			ResetVerticalScale();
			base.Frame = frame;
		}

		void LayoutNaturallyToBounds(CGRect bounds, CGPoint center)
		{
			ResetVerticalScale();
			base.Bounds = bounds;
			base.Center = center;
		}

		void LayoutToFrame(CGRect frame)
		{
			LayoutToBounds(new CGRect(0, 0, frame.Width, frame.Height), new CGPoint(frame.GetMidX(), frame.GetMidY()));
		}

		void LayoutToBounds(CGRect bounds, CGPoint center)
		{
			Transform = CGAffineTransform.MakeIdentity();

			var targetWidth = bounds.Width < 0 ? 0 : bounds.Width;
			_arrangedHeight = bounds.Height < 0 ? 0 : bounds.Height;
			var naturalHeight = GetNaturalHeight(new CGSize(targetWidth, _arrangedHeight));

			if (!IsPositiveFinite(naturalHeight) || !IsPositiveFinite(_arrangedHeight))
			{
				base.Bounds = new CGRect(bounds.X, bounds.Y, targetWidth, 0);
				base.Center = center;
				return;
			}

			base.Bounds = new CGRect(bounds.X, bounds.Y, targetWidth, naturalHeight);
			base.Center = center;
			ApplyVerticalScale();
		}

		void ApplyVerticalScale()
		{
			var naturalHeight = base.Bounds.Height;

			if (!ScalesToArrangedHeight || !IsPositiveFinite(naturalHeight) || !IsPositiveFinite(_arrangedHeight))
			{
				Transform = CGAffineTransform.MakeIdentity();
				return;
			}

			Transform = CGAffineTransform.MakeScale(1, _arrangedHeight / naturalHeight);
		}

		void ResetVerticalScale()
		{
			_arrangedHeight = 0;
			Transform = CGAffineTransform.MakeIdentity();
		}

		nfloat GetNaturalHeight(CGSize size)
		{
			var naturalHeight = SizeThatFits(size).Height;

			if (!IsPositiveFinite(naturalHeight))
			{
				naturalHeight = IntrinsicContentSize.Height;
			}

			if (!IsPositiveFinite(naturalHeight))
			{
				naturalHeight = base.Bounds.Height;
			}

			return naturalHeight;
		}

		static bool IsPositiveFinite(nfloat value)
		{
			var doubleValue = (double)value;
			return doubleValue > 0 && !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue);
		}
	}
}
