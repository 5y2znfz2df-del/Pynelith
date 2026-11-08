// LiquidGlass.fx — WPF 像素着色器 (ps_3_0)，硬件加速。
// 液态玻璃表面效果：圆角 SDF 边缘折射 + 菲涅尔高光 + 顶部光泽 + 着色。
// 与背景层（DWM/Composition backdrop）叠加使用，构成完整“液态玻璃”观感。
// 编译: fxc /T ps_3_0 /E main /Fo LiquidGlass.gshader LiquidGlass.fx

float2 Resolution : register(c0);   // 元素像素尺寸
float  CornerRadius : register(c1); // 圆角半径（像素）
float  EdgeWidth : register(c2);    // 边缘折射/高光带宽度（像素）
float  Refraction : register(c3);   // 折射强度 0..1
float  Highlight : register(c4);    // 高光强度 0..1
float4 Tint : register(c5);         // 着色颜色
float  TintOpacity : register(c6);  // 着色不透明度 0..1

sampler InputSampler : register(s0); // 隐式输入：元素自身 Brush

// 圆角矩形带符号距离场。p 为中心坐标系（像素），b 为半尺寸，r 为圆角。
float sdRoundRect(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 halfSize = Resolution * 0.5;
    float2 p = (uv - 0.5) * Resolution;
    float r = min(CornerRadius, min(halfSize.x, halfSize.y));

    // SDF 数值梯度 → 外法线
    float2 e = float2(1.0, 0.0);
    float2 grad = float2(
        sdRoundRect(p + e.xy, halfSize, r) - sdRoundRect(p - e.xy, halfSize, r),
        sdRoundRect(p + e.yx, halfSize, r) - sdRoundRect(p - e.yx, halfSize, r));
    grad = normalize(grad + 1e-5);

    float d = sdRoundRect(p, halfSize, r);            // 负值在内部
    float edgeWidth = max(EdgeWidth, 1.0);
    float edge = 1.0 - saturate(-d / edgeWidth);      // 靠近边缘 → 1
    edge = edge * edge * (3.0 - 2.0 * edge);          // smoothstep

    // 折射：在边缘带内沿法线向内弯折采样坐标（透镜效果）
    float2 offset = -grad * edge * Refraction * edgeWidth / max(Resolution, float2(1.0, 1.0));
    float4 col = tex2D(InputSampler, uv + offset);

    // 着色：边缘更浓，中心更淡
    col.rgb = lerp(col.rgb, Tint.rgb, TintOpacity * (0.35 + 0.65 * edge));

    // 菲涅尔边缘高光 + 顶部光泽
    float fresnel = pow(edge, 1.5) * Highlight;
    float sheen = saturate(0.55 - uv.y) * 0.30 * Highlight;
    col.rgb += fresnel + sheen;

    col.a = saturate(col.a);
    return col;
}
