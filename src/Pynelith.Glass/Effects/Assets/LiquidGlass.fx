// LiquidGlass.fx — 1:1 移植自 C:\GitHub\liquid-glass-rs\src\main.rs 的 LIQUID_SKSL
//
// 真正的液态玻璃：
//   1. 背景**完全不模糊**，直接采样桌面像素（清晰可见）；
//   2. 圆角矩形 SDF，边缘带内按 bezel 曲率求法线；
//   3. 真实折射（refract + IOR），位移量 = thickness × (0.35 + 0.65·band)；
//   4. RGB 色散；
//   5. 竖向渐变染色（上端近黑 → 下端蓝灰）；
//   6. 边缘带镜面高光 + 1.8px 轮廓（先压暗再叠亮）。
// 编译: fxc /T ps_3_0 /E main /Fo LiquidGlass.gshader LiquidGlass.fx

float2 Resolution     : register(c0);  // 元素尺寸（物理像素）
float2 Center         : register(c1);  // 玻璃中心（元素内坐标）
float4 Shape          : register(c2);  // x=corner y=bezel z=thickness w=ior
float4 Optics         : register(c3);  // x=dispersion y=lightAngle z=tintAmount
float4 TintTop        : register(c4);  // 渐变上端（近黑）
float4 TintBottom     : register(c5);  // 渐变下端（蓝灰）
float2 BackdropSize   : register(c6);  // 抓屏图像尺寸
float2 BackdropOffset : register(c7);  // 元素原点在抓屏图中的偏移

sampler InputSampler : register(s0);

/// 圆角矩形有符号距离场
float sdRoundRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float SceneD(float2 p)
{
    return sdRoundRect(p, Resolution * 0.5, Shape.x);
}

/// 采样抓屏背景：coord 是窗口内坐标，加上抓屏余量偏移后映射到图像 uv。
/// 背景不做任何模糊 —— 这是与"亚克力"的本质区别。
float3 SampleBackdrop(float2 coord)
{
    return tex2D(InputSampler, (coord + BackdropOffset) / max(BackdropSize, float2(1.0, 1.0))).rgb;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 coord = uv * Resolution;
    float2 local = coord - Center;

    float d = SceneD(local);
    if (d > 0.0)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float inward = -d;
    float band = saturate(inward / max(Shape.y, 0.001));

    // 法线：只有边缘带需要；用前向差分（2 次 SDF），退化时回退到矩形解析方向。
    float2 g = float2(0.0, 1.0);
    if (band < 1.0)
    {
        float2 grad = float2(
            SceneD(local + float2(1.0, 0.0)) - d,
            SceneD(local + float2(0.0, 1.0)) - d);
        float glen = length(grad);
        float2 gRect = normalize(local / max(Resolution * 0.5, float2(1.0, 1.0)) + float2(1e-5, 1e-5));
        g = glen > 0.002 ? grad / glen : gRect;
    }

    // 边缘斜率 → 3D 法线 → 折射
    float u = 1.0 - band;
    float slope = band < 1.0
        ? (2.0 * u * u * u) / sqrt(max(1.0 - u * u * u * u, 0.0001))
        : 0.0;
    float3 normal = normalize(float3(g * slope, 1.0));

    float3 incident = float3(0.0, 0.0, -1.0);
    float3 ray = refract(incident, normal, 1.0 / max(Shape.w, 1.0001));

    float2 shift = float2(0.0, 0.0);
    if (ray.z < -0.0001)
    {
        shift = (ray.xy / -ray.z) * Shape.z * (0.35 + 0.65 * band);
    }

    // 背景采样（含色散：RGB 三通道不同位移）
    float3 glass;
    if (Optics.x < 0.001)
    {
        glass = SampleBackdrop(coord + shift);
    }
    else
    {
        glass = float3(
            SampleBackdrop(coord + shift * (1.0 + Optics.x)).r,
            SampleBackdrop(coord + shift).g,
            SampleBackdrop(coord + shift * (1.0 - Optics.x)).b);
    }

    // 竖向渐变染色：上端近黑，下端偏蓝灰
    float gy = saturate((local.y + Resolution.y * 0.5) / max(Resolution.y, 1.0));
    float4 tint = lerp(TintTop, TintBottom, gy);
    glass = lerp(glass, tint.rgb, Optics.z);

    // 边缘镜面高光：只出现在被掰弯的边缘带
    float2 lightDir = float2(cos(Optics.y), sin(Optics.y));
    float edge = 1.0 - band;
    float spec = pow(max(dot(g, lightDir), 0.0), 6.0) * edge * edge;
    glass += spec * 0.85;

    // 轮廓：先压暗一道（白底上才看得见），再叠高光
    float rim = 1.0 - smoothstep(0.0, 1.8, inward);
    glass *= 1.0 - rim * 0.22;
    glass += rim * 0.25;

    float alpha = 1.0 - smoothstep(-1.0, 0.0, d);
    return float4(glass * alpha, alpha);
}
