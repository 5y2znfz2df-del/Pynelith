// LiquidGlass.fx — WPF 像素着色器 (ps_3_0)，硬件加速。
// 苹果风「液态玻璃」(Liquid Glass)：
//   1. 背景来自屏幕实时采样（ImageBrush），做轻模糊 + 高饱和 + 提亮，保持背景可辨认；
//   2. 圆角 SDF 边缘做透镜折射，模拟玻璃厚度对背景的放大/弯折；
//   3. 镜面亮边 + 内侧厚度阴影 + 顶部柔光，塑造玻璃的实体感与高光；
//   4. 圆角外 alpha=0，让分层窗口真正透出桌面。
// 编译: fxc /T ps_3_0 /E main /Fo LiquidGlass.gshader LiquidGlass.fx

float2 Resolution   : register(c0);  // 元素尺寸（DIP/像素）
float  CornerRadius : register(c1);  // 圆角半径（像素）
float  EdgeBand     : register(c2);  // 边缘折射带宽度（像素）
float  Refraction   : register(c3);  // 折射强度 0..1
float  BlurRadius   : register(c4);  // 背景模糊半径（像素）
float  Saturation   : register(c5);  // 背景饱和度倍数（>1 提升）
float  Brightness   : register(c6);  // 背景亮度倍数
float4 Tint         : register(c7);  // 玻璃着色
float  TintOpacity  : register(c8);  // 着色不透明度 0..1
float  Highlight    : register(c9);  // 高光强度 0..1

sampler InputSampler : register(s0); // 背景采样（屏幕截图）

// 圆角矩形带符号距离场：p 为中心坐标系，b 为半尺寸，r 为圆角半径。
float sdRoundRect(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// 9-tap 高斯模糊（权重和 = 1），半径由 BlurRadius 控制。
float3 BlurredBackground(float2 uv, float2 texel)
{
    float2 o = texel * max(BlurRadius, 0.0);

    float3 c = tex2D(InputSampler, uv).rgb * 0.25;
    c += (tex2D(InputSampler, uv + float2( o.x, 0.0)).rgb
        + tex2D(InputSampler, uv + float2(-o.x, 0.0)).rgb
        + tex2D(InputSampler, uv + float2(0.0,  o.y)).rgb
        + tex2D(InputSampler, uv + float2(0.0, -o.y)).rgb) * 0.125;
    c += (tex2D(InputSampler, uv + float2( o.x,  o.y)).rgb
        + tex2D(InputSampler, uv + float2( o.x, -o.y)).rgb
        + tex2D(InputSampler, uv + float2(-o.x,  o.y)).rgb
        + tex2D(InputSampler, uv + float2(-o.x, -o.y)).rgb) * 0.0625;

    return c;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 halfSize = Resolution * 0.5;
    float2 texel = 1.0 / max(Resolution, float2(1.0, 1.0));
    float r = min(CornerRadius, min(halfSize.x, halfSize.y));

    float2 p = (uv - 0.5) * Resolution;
    float d = sdRoundRect(p, halfSize, r);   // 负值在内部

    // 圆角抗锯齿：轮廓外 alpha 归 0，让分层窗口透出桌面。
    float alpha = saturate(-d + 0.5);

    // 外法线（SDF 数值梯度）
    float2 grad = float2(
        sdRoundRect(p + float2(1.0, 0.0), halfSize, r) - sdRoundRect(p - float2(1.0, 0.0), halfSize, r),
        sdRoundRect(p + float2(0.0, 1.0), halfSize, r) - sdRoundRect(p - float2(0.0, 1.0), halfSize, r));
    grad = normalize(grad + float2(1e-5, 1e-5));

    // 边缘带权重：靠近轮廓 → 1，向内衰减到 0
    float edgeBand = max(EdgeBand, 1.0);
    float edge = saturate(1.0 + d / edgeBand);
    edge = edge * edge;   // 越靠外越陡，折射集中在边缘

    // 透镜折射：沿内法线偏移采样，把外部背景"吸入"边缘，形成液体透镜。
    float2 offset = -grad * edge * Refraction * edgeBand * texel;
    float3 col = BlurredBackground(uv + offset, texel);

    // 背景增强：高饱和 + 轻微提亮（苹果玻璃保留背景色彩，而不是像 Acrylic 那样去饱和发灰）
    float lum = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(float3(lum, lum, lum), col, Saturation) * Brightness;

    // 玻璃着色：整体极淡，边缘略浓
    col = lerp(col, Tint.rgb, TintOpacity * (0.25 + 0.75 * edge));

    // 内侧厚度阴影：紧贴边缘的一圈柔暗，表现玻璃体的体量
    float innerShadow = saturate(1.0 - (-d) / (edgeBand * 0.55));
    col *= 1.0 - innerShadow * 0.10;

    // 环境反射：左上方大面积柔光（苹果玻璃上表面掠射高光）
    float2 lightDir = normalize(float2(-0.65, -1.0));
    float envGrad = saturate(dot(uv - 0.5, lightDir) + 0.55);
    float envGlow = pow(envGrad, 2.4) * 0.20 * Highlight;

    // 边缘内发光 + 顶部柔光
    float edgeGlow = pow(edge, 2.0) * 0.30 * Highlight;
    float topGlow = saturate(1.0 - uv.y / 0.62) * 0.14 * Highlight;
    col += (envGlow + edgeGlow + topGlow) * float3(1.0, 1.0, 1.0);

    // 镜面亮边：轮廓内侧约 2.6px 的高亮细线（液态玻璃的标志性高光）
    float rim = smoothstep(2.6, 0.0, -d) * Highlight;
    col = lerp(col, float3(1.0, 1.0, 1.0), saturate(rim * 0.9));

    return float4(col * alpha, alpha);
}
