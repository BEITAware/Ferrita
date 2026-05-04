using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Skyweaver.Rendering;

namespace Skyweaver.Controls.SkyweaverPreferencesControl.Views
{
    public sealed class PreferencesBlueRippleBackgroundEffect : ShaderEffect
    {
        private const string ShaderSource = @"
sampler2D inputSampler : register(s0);
float time : register(c0);
float aspectRatio : register(c1);

float hash(float n)
{
    return frac(sin(n) * 43758.5453);
}

float noise(float2 x)
{
    float2 i = floor(x);
    float2 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);

    float n = i.x + i.y * 57.0;
    float a = hash(n + 0.0);
    float b = hash(n + 1.0);
    float c = hash(n + 57.0);
    float d = hash(n + 58.0);

    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float softBand(float2 p, float center, float width, float amp, float freq, float speed, float phase)
{
    float curve = center;
    curve += sin(p.x * freq + time * speed + phase) * amp;
    curve += sin(p.x * (freq * 0.47) - time * (speed * 0.62) + phase * 1.7) * (amp * 0.48);

    return exp(-abs(p.y - curve) * width);
}

float ripple(float2 p, float2 center, float radius, float speed, float phase)
{
    float2 q = p - center;
    float dist = length(q);
    float ring = sin(dist * radius - time * speed + phase);
    float envelope = exp(-dist * dist * 0.72);

    return ring * envelope;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 source = tex2D(inputSampler, uv);
    float2 p = float2((uv.x - 0.5) * 2.0 * aspectRatio, (0.5 - uv.y) * 2.0);

    float waterShift = sin(p.y * 2.35 + time * 0.42) * 0.045;
    waterShift += sin(p.x * 1.65 - time * 0.31) * 0.032;
    float2 q = p + float2(waterShift, -waterShift * 0.55);

    float topMix = smoothstep(0.0, 0.62, uv.y);
    float bottomMix = smoothstep(0.52, 1.0, uv.y);
    float3 topBlue = float3(0.04, 0.64, 0.98);
    float3 centerBlue = float3(0.0, 0.20, 0.58);
    float3 bottomBlue = float3(0.0, 0.52, 0.98);
    float3 baseColor = lerp(topBlue, centerBlue, topMix);
    baseColor = lerp(baseColor, bottomBlue, bottomMix * 0.45);

    float leftTopGlow = exp(-dot(q - float2(-aspectRatio * 0.9, 0.94), q - float2(-aspectRatio * 0.9, 0.94)) * 1.15);
    float leftBottomGlow = exp(-dot(q - float2(-aspectRatio * 0.88, -0.92), q - float2(-aspectRatio * 0.88, -0.92)) * 1.05);
    float centerGlow = exp(-dot(q - float2(-0.22, 0.02), q - float2(-0.22, 0.02)) * 0.58);
    float rightShade = exp(-dot(q - float2(aspectRatio * 0.82, 0.04), q - float2(aspectRatio * 0.82, 0.04)) * 0.5);
    float normalizedX = q.x / max(aspectRatio, 0.0001);
    float leftHalfMask = saturate((-normalizedX + 0.18) / 1.15);
    float leftEdgeMask = saturate((-normalizedX - 0.12) / 0.78);

    baseColor += float3(0.02, 0.48, 0.82) * leftTopGlow * 0.40;
    baseColor += float3(0.0, 0.50, 0.92) * leftBottomGlow * 0.36;
    baseColor += float3(0.02, 0.18, 0.42) * centerGlow * 0.30;
    baseColor -= float3(0.01, 0.07, 0.18) * leftEdgeMask * 0.22;
    baseColor -= float3(0.03, 0.10, 0.27) * rightShade * 0.82;

    float broadA = softBand(q, 0.28, 2.35, 0.24, 0.82, 0.26, 0.4);
    float broadB = softBand(q, -0.06, 3.0, 0.18, 1.05, -0.22, 2.2);
    float broadC = softBand(q, -0.52, 3.55, 0.19, 0.72, 0.18, 4.1);

    float sheen = softBand(q, 0.08, 8.8, 0.07, 1.65, 0.42, 1.4);
    sheen += softBand(q, -0.33, 10.5, 0.06, 1.42, -0.35, 3.2) * 0.7;

    float rippleA = ripple(q, float2(-0.12, 0.14), 8.0, 0.78, 0.6);
    float rippleB = ripple(q, float2(0.56, -0.2), 6.6, -0.62, 2.4);
    float linearRipple = sin(q.x * 3.2 + q.y * 1.7 + time * 0.58) * 0.5;
    linearRipple += sin(q.x * 1.7 - q.y * 2.3 - time * 0.46) * 0.5;
    float rollingWave = sin(q.x * 1.15 + q.y * 3.8 + time * 0.36);
    rollingWave += sin(q.x * 2.8 - q.y * 1.45 - time * 0.52) * 0.55;

    float rippleMask = saturate(0.96 - length(q * float2(0.38, 0.72)) * 0.2);
    float fineNoise = noise(q * 2.1 + float2(time * 0.02, -time * 0.018)) - 0.5;

    float3 bandColor = float3(0.03, 0.22, 0.60) * broadA * 0.34;
    bandColor += float3(0.08, 0.42, 0.86) * broadB * 0.28;
    bandColor += float3(0.0, 0.30, 0.72) * broadC * 0.30;
    bandColor += float3(0.58, 0.84, 1.0) * sheen * 0.16;

    float rippleLight = (rippleA * 0.45 + rippleB * 0.32 + linearRipple * 0.18) * rippleMask;
    float3 finalColor = baseColor + bandColor;
    finalColor += float3(0.10, 0.30, 0.58) * rippleLight * 0.24;
    finalColor += float3(0.05, 0.20, 0.46) * rollingWave * rippleMask * 0.055;
    finalColor += fineNoise * 0.026;
    finalColor *= 1.0 - leftHalfMask * 0.18;

    float vignette = saturate(1.08 - length(p * float2(0.62, 0.96)) * 0.34);
    finalColor *= 0.56 + vignette * 0.12;
    finalColor = finalColor / (1.0 + finalColor * 0.18);
    finalColor *= 0.90;
    finalColor = saturate(finalColor);

    return float4(finalColor * max(source.a, 0.0001), source.a);
}";

        private static readonly PixelShader SharedPixelShader = CreatePixelShader();

        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            nameof(Input),
            typeof(PreferencesBlueRippleBackgroundEffect),
            0);

        public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
            nameof(Time),
            typeof(double),
            typeof(PreferencesBlueRippleBackgroundEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(PreferencesBlueRippleBackgroundEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(1)));

        public PreferencesBlueRippleBackgroundEffect()
        {
            PixelShader = SharedPixelShader;

            UpdateShaderValue(InputProperty);
            UpdateShaderValue(TimeProperty);
            UpdateShaderValue(AspectRatioProperty);
        }

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public double Time
        {
            get => (double)GetValue(TimeProperty);
            set => SetValue(TimeProperty, value);
        }

        public double AspectRatio
        {
            get => (double)GetValue(AspectRatioProperty);
            set => SetValue(AspectRatioProperty, value);
        }

        private static PixelShader CreatePixelShader()
        {
            var shader = new PixelShader();
            var shaderBytes = DirectXShaderCompiler.CompilePixelShader(ShaderSource);

            using var stream = new MemoryStream(shaderBytes, writable: false);
            shader.SetStreamSource(stream);
            shader.Freeze();
            return shader;
        }
    }
}
