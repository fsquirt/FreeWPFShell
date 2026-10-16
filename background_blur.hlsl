Texture2D shaderTexture : register(t0);
Texture2D imageTexture : register(t1);
SamplerState samplerState : register(s0);

cbuffer PixelShaderSettings
{
    float time;
    float scale;
    float2 resolution;
    float4 background;
    float4 customParams;
};

static const float M_PI = 3.14159265f;

// customParams.x = 拉伸模式（原生控件传入）
// customParams.y = 背景图“降暗比例”的负值（0 = 原图亮度，-0.1 = 亮度降低 10%，-1.0 = 全黑）
//                  未设置时原生控件传 0，正好表示不降暗
static const float DEFAULT_IMAGE_BRIGHTNESS = 1.0f;

float Gaussian2D(float x, float y, float sigma)
{
    return 1 / (sigma * sqrt(2 * M_PI)) * exp(-0.5 * (x * x + y * y) / (sigma * sigma));
}

// 采样背景图并应用亮度系数（纯色背景不参与变暗）
float4 SampleImage(float2 uv, bool mapImage, float brightness)
{
    float4 c = mapImage ? imageTexture.Sample(samplerState, uv) : background;
    if (mapImage)
    {
        c.rgb *= brightness;
    }
    return c;
}

float4 main(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET
{
    float width, height;
    imageTexture.GetDimensions(width, height);
    float texelWidth = 1.0f / width;
    float texelHeight = 1.0f / height;

    float2 rw = resolution;
    float2 iw = float2(width, height);
    float2 imgUV = tex; // Stretch (Mode 0) by default
    
    int mode = (int)(customParams.x + 0.1f);
    
    if (mode == 1 || mode == 5) // Fill or Span
    {
        float scaleFac = max(rw.x / iw.x, rw.y / iw.y);
        float2 scaledIw = iw * scaleFac;
        float2 offset = (scaledIw - rw) / 2.0f;
        imgUV = (tex * rw + offset) / scaledIw;
    }
    else if (mode == 2) // Fit
    {
        float scaleFac = min(rw.x / iw.x, rw.y / iw.y);
        float2 scaledIw = iw * scaleFac;
        float2 offset = (rw - scaledIw) / 2.0f;
        imgUV = (tex * rw - offset) / scaledIw;
    }
    else if (mode == 3) // Tile
    {
        imgUV = frac((tex * rw) / iw);
    }
    else if (mode == 4) // Center
    {
        float2 offset = (rw - iw) / 2.0f;
        imgUV = (tex * rw - offset) / iw;
    }
    
    bool mapImage = true;
    if (mode == 2 || mode == 4) {
        if (imgUV.x < 0.0f || imgUV.x > 1.0f || imgUV.y < 0.0f || imgUV.y > 1.0f) {
            mapImage = false;
        }
    }

    float4 termColor = shaderTexture.Sample(samplerState, tex);

    // 背景图亮度系数：1.0 = 原图，0.9 = 亮度降低 10%
    float imgBrightness = DEFAULT_IMAGE_BRIGHTNESS + min(customParams.y, 0.0f);

    // 1. 背景高斯模糊
    float4 bgBlurred = float4(0, 0, 0, 0);
    float blurWeightSum = 0;

    for (float bx = -4; bx <= 4; bx++)
    {
        for (float by = -4; by <= 4; by++)
        {
            float w = Gaussian2D(bx, by, 12.0f);
            float2 sUV = imgUV + float2(bx * texelWidth * 8.0f, by * texelHeight * 8.0f);
            bgBlurred += SampleImage(sUV, mapImage, imgBrightness) * w;
            blurWeightSum += w;
        }
    }
    bgBlurred /= blurWeightSum;

    // 2. 文字遮罩：文字存在的地方才用模糊背景
    float textMask = 0;
    float maskWeightSum = 0;

    for (float mx = -5; mx <= 5; mx++)
    {
        for (float my = -5; my <= 5; my++)
        {
            float w = Gaussian2D(mx, my, 8.0f);
            textMask += shaderTexture.Sample(samplerState, tex + float2(mx * texelWidth * 4.5f, my * texelHeight * 4.5f)).a * w;
            maskWeightSum += w;
        }
    }
    textMask /= maskWeightSum;

    float blurMask = smoothstep(0.0005f, 0.02f, textMask);

    // 3. 合成：文字下方显示模糊背景，其余显示原始背景，文字贴在顶层
    float4 bgNormal = SampleImage(imgUV, mapImage, imgBrightness);
    float4 finalBg = lerp(bgNormal, bgBlurred, blurMask);

    float3 finalColor = termColor.rgb + finalBg.rgb * (1.0f - termColor.a);

    return float4(finalColor, 1.0f);
}
