// LiquidGlass.fx — 忠实移植 WinIsland 的液态玻璃着色器
// (crates/winisland-render/src/shaders/liquid.sksl，去掉 LingoSoul/Siri 波段与多形状 union)
// 原理：预模糊背景贴图 + 超椭圆圆角 SDF + 边缘斜率法线 + 真实折射(斯涅尔) + 色散 + 镜面光。
// 编译: fxc /T ps_3_0 /E main /Fo LiquidGlass.gshader LiquidGlass.fx

float2 Resolution   : register(c0);  // 物理像素尺寸
float4 Corner       : register(c1);  // x=extent(超椭圆半径) y=exponent
float4 Bezel        : register(c2);  // x=bezelWidth y=thickness z=glassIor w=displacementFactor
float4 Optics       : register(c3);  // x=dispersion y=progress z=opacity w=legibility
float4 Tint         : register(c4);  // rgba
float2 LightDir     : register(c5);  // 归一化光照方向
float4 Specular     : register(c6);  // x=strength y=opposite z=opacity w=falloff
float2 SpecShape    : register(c7);  // x=width y=sharpness
float4 Stroke       : register(c8);  // x=width y=opacity z=offsetY
float4 ShadowCfg    : register(c9);  // xy=offset z=radius
float4 Extra        : register(c10); // x=shadowSpread y=shadowAlpha z=edgeSoftness w=edgeOffset
float2 Extra2       : register(c11); // x=backgroundBlur y=blackFade

sampler InputSampler : register(s0);

static const float kMaxSlope = 11.430052;
static const float kEpsilon = 0.0001;

// 超椭圆（continuous corner）有符号距离场；g 返回外法线。
float sdRoundRect(float2 p, float4 s, float2 c, out float2 g)
{
    float2 sgn = float2(p.x < s.x ? -1.0 : 1.0, p.y < s.y ? -1.0 : 1.0);
    float2 q = abs(p - s.xy) - s.zw + c.x;

    if (q.x > 0.0 && q.y > 0.0)
    {
        float2 m = q / c.x;
        float2 mp = pow(m, float2(c.y - 1.0, c.y - 1.0));
        g = normalize(mp) * sgn;
        return (pow(mp.x * m.x + mp.y * m.y, 1.0 / c.y) - 1.0) * c.x;
    }

    g = q.x > q.y ? float2(sgn.x, 0.0) : float2(0.0, sgn.y);
    return max(q.x, q.y) - c.x;
}

// 背景采样：WinIsland 的 backdrop 是已模糊的贴图（HostBackdrop + D2D GaussianBlur），
// 这里由 Skia 预先生成模糊贴图，着色器只做一次纹理采样。
float3 Backdrop(float2 p)
{
    float2 uv = p / Resolution + 0.5;
    return tex2D(InputSampler, uv).rgb;
}

// 外描边 + 投影（在填充区域之外合成）
float Underlay(float2 p, float d, float2 g, float coverage)
{
    float4 s = float4(0.0, 0.0, Resolution * 0.5);
    float2 corner = Corner.xy;

    float strokeA = 0.0;
    if (Stroke.y > 0.0)
    {
        float outset = max(Stroke.x * g.x * g.x + Stroke.z * max(g.y, 0.0), 0.0);
        strokeA = clamp((1.0 - smoothstep(outset - 0.5, outset + 0.5, d)) * smoothstep(-0.5, 0.5, d), 0.0, 1.0)
                * Stroke.y * coverage;
    }

    float shadowA = 0.0;
    if (Extra.y > 0.0)
    {
        float2 unused;
        float ds = sdRoundRect(p - ShadowCfg.xy, s, corner, unused) - Extra.x;
        float r = max(ShadowCfg.z, 0.5);
        shadowA = (1.0 - smoothstep(-r, r, ds)) * Extra.y * coverage;
    }

    return strokeA + shadowA * (1.0 - strokeA);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * Resolution;

    float t = saturate(Optics.y);
    if (t <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float pulse = 4.0 * t * (1.0 - t);
    float coverage = smoothstep(0.0, 0.55, t);
    float opacity = saturate(Optics.z);

    float4 s = float4(0.0, 0.0, Resolution * 0.5);
    float2 corner = Corner.xy;

    float2 g;
    float d = sdRoundRect(p, s, corner, g);

    float softness = max(Extra.z, 0.35);
    float edgeOffset = Extra.w;
    float fill = 1.0 - smoothstep(edgeOffset - softness, edgeOffset + softness, d);

    float under = fill < 1.0 ? Underlay(p, d, g, coverage) : 0.0;
    if (fill <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, under * opacity);
    }

    // ---- 边缘透镜：按 bezel 曲率求法线，再走真实折射 ----
    float inward = max(-d, 0.0);
    float band = saturate(inward / max(Bezel.x, 1.0));
    float u = 1.0 - band;
    float u2 = u * u;
    float slope = inward <= Bezel.x
        ? min(2.0 * u2 * u / sqrt(max(1.0 - u2 * u2, kEpsilon)), kMaxSlope)
        : 0.0;

    float3 normal = normalize(float3(g * slope, 1.0));
    float3 ray = refract(float3(0.0, 0.0, -1.0), normal, 1.0 / max(Bezel.z, 1.0001));
    float2 shift = ray.z < 0.0
        ? ray.xy / -ray.z * (Bezel.y + inward * 0.5) * Bezel.w
        : float2(0.0, 0.0);
    shift *= t + 7.0 * t * (1.0 - t);

    // ---- 色散：RGB 三通道不同折射偏移 ----
    float spread = Optics.x;
    float3 glass;
    if (dot(shift, shift) < 0.0025)
    {
        glass = Backdrop(p);
    }
    else if (spread < 0.001)
    {
        glass = Backdrop(p + shift);
    }
    else
    {
        glass = float3(
            Backdrop(p + shift * (1.0 + spread)).r,
            Backdrop(p + shift).g,
            Backdrop(p + shift * (1.0 - spread)).b);
    }

    // ---- 着色 + 可读性补偿 ----
    float luma = dot(glass, float3(0.2126, 0.7152, 0.0722));
    float tintAmount = min(Tint.a + Optics.w * smoothstep(0.35, 0.95, luma), 1.0);
    glass = lerp(glass, Tint.rgb, tintAmount * coverage);

    // ---- 黑色渐隐（可选）----
    if (Extra2.y > 0.0)
    {
        float top = -s.w;
        float height = saturate((p.y - top) / max(s.w * 2.0, 1.0));
        float black = 1.0 - smoothstep(0.15, 0.85, height);
        glass = lerp(glass, float3(0.0, 0.0, 0.0), Extra2.y * black);
    }

    // ---- 镜面高光：沿边缘法线的方向光 + 反向补光 ----
    float rim = 1.0 - smoothstep(0.0, max(SpecShape.x, 0.25), abs(d));
    if (rim > 0.0)
    {
        float falloff = Specular.w * band * band;
        float lit = pow(max(dot(g, LightDir), 0.0), SpecShape.y) * max(Specular.x - falloff, 0.0);
        float opposite = pow(max(dot(g, -LightDir), 0.0), SpecShape.y) * max(Specular.y - falloff, 0.0);
        glass += clamp((lit + opposite) * rim, 0.0, 1.0) * Specular.z * min(coverage + pulse * 0.85, 1.75);
    }

    float alpha = fill + under * (1.0 - fill);
    return float4(glass * fill * opacity, alpha * opacity);
}
