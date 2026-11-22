// LensGlow.fx —— Siri 液态玻璃透镜
//
// 视觉目标（对齐 iOS 27 Beta Siri 参考图）：
//   1. 玻璃球体（元素为正方形时 SDF 即正圆）：背景**完全不模糊**，直接采样桌面像素；
//   2. 边缘一圈真实折射：壁纸画面穿过玻璃边缘发生弯曲（lens distortion）；
//   3. 中心横向色散光带：中心亮白，向两端过渡到浅蓝 / 暖橙；
//   4. 光带由状态驱动：收音脉动、生成流动、空闲呼吸、收尾收拢变暗。
//
// 与 LiquidGlass.fx 的区别：SDF 是**椭圆**（正方形元素下即正圆）而非圆角矩形，
// 且多了一组「光带」参数（c9），让光效可以按状态连续变化。
//
// 编译: fxc /T ps_3_0 /E main /Fo LensGlow.gshader LensGlow.fx

float2 Resolution     : register(c0);  // 元素尺寸（物理像素）
float2 Center         : register(c1);  // 玻璃中心（元素内坐标）
float4 Shape          : register(c2);  // x=bezel y=thickness z=ior w=reserved
float4 Optics         : register(c3);  // x=dispersion y=lightAngle z=tintAmount w=surfaceOpacity
float4 TintTop        : register(c4);  // 染色上端
float4 TintBottom     : register(c5);  // 染色下端
float2 BackdropSize   : register(c6);  // 抓屏图像尺寸
float2 BackdropOffset : register(c7);  // 元素原点在抓屏图中的偏移
float4 Composite      : register(c8);  // x=内部染色权重 y=透镜展开度 z=光带强度 w=光带收拢度
float4 Band           : register(c9);  // x=光带半宽 y=光带核心亮度 z=冷色权重 w=暖色权重
float4 Horizon        : register(c10); // x=黑白分界 y=过渡宽度 z/w=预留

sampler InputSampler : register(s0);

/// 椭圆有符号距离场（隐函数一阶近似）
///
/// 不能用 iq 的 length 近似：它在椭圆中心返回 0（等于边界），
/// 会让 coverage 在中心恒为 0，光带永远画不出来。
/// 这里用归一化隐函数 F=(x/a)²+(y/b)²-1 除以梯度模长：
/// 中心 F=-1、梯度为 0，单独兜底为 -min(a,b)，深度才正确。
float sdEllipse(float2 p, float2 ab)
{
    float2 safe = max(ab, float2(0.001, 0.001));
    float2 n = p / safe;
    float f = dot(n, n) - 1.0;
    float2 grad = 2.0 * p / (safe * safe);
    float g = length(grad);
    return g > 1e-6 ? f / g : -min(safe.x, safe.y);
}

/// 椭圆的解析梯度方向（指向外侧）
float2 ellipseNormal(float2 p, float2 ab)
{
    float2 safe = max(ab, float2(0.001, 0.001));
    float2 grad = 2.0 * p / (safe * safe);
    float g = length(grad);
    return g > 1e-6 ? grad / g : float2(0.0, -1.0);
}

/// 场景距离场
float SceneD(float2 p)
{
    return sdEllipse(p, Resolution * 0.5);
}

/// 采样抓屏背景：背景不做任何模糊 —— 这是与"亚克力"的本质区别。
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
    float band = saturate(inward / max(Shape.x, 0.001));

    // 法线：椭圆的解析梯度方向（比 SDF 差分更准也更省）
    float2 g = ellipseNormal(local, Resolution * 0.5);

    // 边缘斜率 → 3D 法线 → 折射
    float u = 1.0 - band;
    float slope = band < 1.0
        ? (2.0 * u * u * u) / sqrt(max(1.0 - u * u * u * u, 0.0001))
        : 0.0;
    float3 normal = normalize(float3(g * slope, 1.0));

    float3 incident = float3(0.0, 0.0, -1.0);
    float3 ray = refract(incident, normal, 1.0 / max(Shape.z, 1.0001));

    float2 shift = float2(0.0, 0.0);
    if (ray.z < -0.0001)
    {
        shift = (ray.xy / -ray.z) * Shape.y * (0.35 + 0.65 * band);
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

    // ---- 上黑下透：竖向双区玻璃 ----
    // 上半边是黑色玻璃，下半边是完全透明的玻璃，两区之间平滑过渡。
    // 关键点：染色只叠在**折射采样结果**之上，而不是盖掉它 ——
    // 所以黑色区依旧带着液态玻璃的折弯与色散，只是被深色压暗；
    // 透明区则原样输出折射画面，属于"看得见桌面但轮廓被弯折"的纯玻璃。
    float gy = saturate((local.y + Resolution.y * 0.5) / max(Resolution.y, 1.0));
    float4 tint = lerp(TintTop, TintBottom, gy);

    float split = saturate(Horizon.x);
    float soft = max(Horizon.y, 0.001);

    // 1 = 完全在上半区（黑玻璃），0 = 完全在下半区（透明玻璃）
    float upper = 1.0 - smoothstep(split - soft, split + soft, gy);

    float darkWeight = saturate(Optics.z) * upper;
    glass = lerp(glass, tint.rgb, darkWeight);
    // 深色玻璃再压一点亮度：桌面亮部（白窗口）不会把黑色冲成灰
    glass *= 1.0 - darkWeight * 0.16;

    // 从这里往下的亮部（高光 / Fresnel / 轮廓 / 光带）都是"自发光"，
    // 与背景折射分开累计：录屏兼容模式下内部只输出染色 alpha、真实桌面
    // 由窗口自身透出，这时自发光必须单独保留，否则横光会跟着背景一起消失。
    float3 emissive = float3(0.0, 0.0, 0.0);

    // 边缘镜面高光：贴着边缘的一道窄光斑
    float2 lightDir = float2(cos(Optics.y), sin(Optics.y));
    float edge = 1.0 - band;
    float spec = pow(max(dot(g, lightDir), 0.0), 14.0) * pow(edge, 4.0);
    emissive += spec * 0.34;

    // Fresnel 边缘反射：法线越侧向反射越强
    float fresnel = pow(1.0 - saturate(normal.z), 3.0);
    emissive += fresnel * 0.12 * float3(0.85, 0.92, 1.0);

    // 轮廓：只保留一圈很细的亮边
    float rim = 1.0 - smoothstep(0.0, 1.4, inward);
    float topness = pow(saturate(0.5 - g.y * 0.5), 2.2);
    glass *= 1.0 - rim * 0.12;
    emissive += rim * (0.02 + 0.16 * topness);

    // ---- 中心横向色散光带 ----
    // 目标形态（对齐参考图）：一条**横贯椭圆**的扁平色散带，
    // 中心亮白，向左右两端过渡为浅蓝 / 暖橙，纵向只有很窄的一层。
    //
    // 横向用「相对椭圆半宽」归一化，端点落在 nx≈1 处（贴到椭圆边缘）；
    // 纵向用高斯把光带压成扁平的一层。
    // 光带活在玻璃**内部**，横向位置要跟着折射走：
    // 透镜左右边缘横向放大最明显，光带两端因此被压缩、收窄，
    // 看起来是"埋在球体里"的一条光，而不是贴在球面外的一根直线。
    // 纵向保持几何中心（球对称下光带本来就在中心平面，不能整体平移，
    // 否则会被推到球体顶部）。
    float2 bandLocal = float2(local.x - shift.x * 0.60, local.y);

    float halfW = max(Resolution.x * 0.5, 0.001);
    float halfH = max(Resolution.y * 0.5, 0.001);
    float nx = saturate(abs(bandLocal.x) / halfW);
    float ny = local.y / halfH;

    // 收拢度：1 = 完全收拢到中心（关闭动画），0 = 正常展开
    float collapse = saturate(Composite.w);

    // 横向：光带从中心一直铺到边缘（halfWidth=1），中间密两端疏
    float bandHalfWidth = max(0.05, Band.x * (1.0 - collapse * 0.92));

    // 横向衰减：0..1，中心为 1，到 bandHalfWidth 处降到 0
    float across = saturate(1.0 - nx / bandHalfWidth);
    float core = pow(across, 1.35);
    float halo = pow(across, 0.45);

    // 纵向：高斯衰减（把光带压扁成横向细带）
    // 指数越大光带越薄。参考图里光带只占椭圆高度的约 1/4，
    // 且略微偏下（光学上色散带通常不在几何中心）。
    float vert = exp(-ny * ny * 52.0);
    float vertOffset = (local.y - Resolution.y * 0.04) / halfH;
    float vertShifted = exp(-vertOffset * vertOffset * 52.0);

    float intensity = Composite.z;               // 光带总强度（由状态驱动）
    float bright = core * vertShifted * intensity;

    // 色散配色：中心白光，两端浅蓝 / 暖橙
    float3 warm = float3(1.00, 0.58, 0.24);
    float3 cool = float3(0.30, 0.68, 1.00);
    float3 white = float3(1.00, 0.98, 0.94);

    // 冷暖按横向位置分：左冷右暖（与参考图一致）
    float side = saturate(bandLocal.x / halfW * 0.5 + 0.5);
    float3 edgeTint = lerp(cool, warm, side);

    float3 bandColor = lerp(edgeTint, white, core);
    bandColor = lerp(bandColor, white, saturate(Band.y));

    float haloAmount = halo * vert * intensity * 0.42;
    float3 haloColor = lerp(edgeTint, white, 0.25);

    emissive += bandColor * bright * Band.y * 1.55;
    emissive += haloColor * haloAmount;

    glass += emissive;

    float coverage = 1.0 - smoothstep(-1.0, 0.0, d);

    float opacity = saturate(Optics.w);
    if (opacity <= 0.001)
    {
        opacity = 1.0;
    }

    // 录屏兼容模式（Composite.x = 1）：内部不再输出折射采样，真实桌面由
    // 窗口自身透出。此时 alpha 必须按"上黑下透"分区给：
    //   - 上半区：深色玻璃，alpha = TintAmount
    //   - 下半区：完全透明，alpha = 0
    //   - 自发光（横光 / 高光）：按自身亮度把 alpha 撑起来，否则整条光带
    //     会跟着背景一起消失 —— 这正是之前兼容模式下横光"消失感"的来源。
    float compat = saturate(Composite.x);
    float emiAlpha = saturate(dot(emissive, float3(0.33, 0.33, 0.34)));

    float darkAlpha = saturate(Optics.z) * saturate(upper + emiAlpha * 2.0);
    float interiorAlpha = lerp(1.0, darkAlpha, compat);
    float3 interiorColor = lerp(glass, tint.rgb + emissive, compat);

    float surfaceAlpha = coverage * opacity * interiorAlpha;

    // 展开度：透镜从胶囊里"长出来"时，内部先透明再变实
    float reveal = saturate(Composite.y);
    surfaceAlpha *= smoothstep(0.0, 0.35, reveal);

    return float4(interiorColor * surfaceAlpha, surfaceAlpha);
}
