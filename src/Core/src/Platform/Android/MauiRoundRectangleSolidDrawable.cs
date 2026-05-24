using Android.Graphics.Drawables;
using AColor = Android.Graphics.Color;

namespace Microsoft.Maui.Platform
{
	sealed class MauiRoundRectangleSolidDrawable : GradientDrawable
	{
		readonly float[] _cornerRadii = new float[8];

		int? _color;
		CornerRadius _cornerRadius;
		float _density;

		public MauiRoundRectangleSolidDrawable()
		{
			SetShape(ShapeType.Rectangle);
		}

		public void Update(AColor color, CornerRadius cornerRadius, float density)
		{
			var argb = color.ToArgb();
			if (_color != argb)
			{
				_color = argb;
				SetColor(color);
			}

			if (_cornerRadius == cornerRadius && _density == density)
				return;

			_cornerRadius = cornerRadius;
			_density = density;

			_cornerRadii[0] = _cornerRadii[1] = ToPixels(cornerRadius.TopLeft);
			_cornerRadii[2] = _cornerRadii[3] = ToPixels(cornerRadius.TopRight);
			_cornerRadii[4] = _cornerRadii[5] = ToPixels(cornerRadius.BottomRight);
			_cornerRadii[6] = _cornerRadii[7] = ToPixels(cornerRadius.BottomLeft);

			SetCornerRadii(_cornerRadii);
		}

		float ToPixels(double value) => (float)(value * _density);
	}
}
