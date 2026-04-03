// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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

float Gaussian2D(float x, float y, float sigma)
{
    float M_PI = 3.14159265f;
    return 1 / (sigma * sqrt(2 * M_PI)) * exp(-0.5 * (x * x + y * y) / (sigma * sigma));
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
    float4 bgNormal = mapImage ? imageTexture.Sample(samplerState, imgUV) : background;


    // 1. 更大范围的高斯模糊采样 (Stronger Background Blur)
    // 使用更大的 Sigma 和两倍的 Stride (8.0)，让图片背景的模糊程度大幅提升
    float blurSigma = 12.0f;
    float4 bgBlurred = float4(0, 0, 0, 0);
    float blurWeightSum = 0;
    
    for (float bx = -4; bx <= 4; bx++)
    {
        for (float by = -4; by <= 4; by++)
        {
            float w = Gaussian2D(bx, by, blurSigma);
            // 使用 imgUV 替代 tex 进行背景探测偏移，并处理边界外露为纯色
            float2 sUV = imgUV + float2(bx * texelWidth * 8.0f, by * texelHeight * 8.0f);
            float4 sColor = mapImage ? imageTexture.Sample(samplerState, sUV) : background;
            bgBlurred += sColor * w;
            blurWeightSum += w;
        }
    }
    bgBlurred /= blurWeightSum;
    
    // 2. 探测文字以生成【底层区域高斯模糊的遮罩】和【描边染色遮罩】
    // 再次大幅向外扩张探测边界！增加 maskSigma 让边缘更加缓和顺滑
    float maskSigma = 8.0f;
    float maskWeightSum = 0;
    float textHitWeightSum = 0;
    
    float textAlphaSum = 0;
    float3 textColorSum = float3(0, 0, 0);
    
    // Stride 为 4.5f，探测半径延伸至超 22 像素以上，足够覆盖极远处的区域
    for (float ix = -5; ix <= 5; ix++)
    {
        for (float iy = -5; iy <= 5; iy++)
        {
            float w = Gaussian2D(ix, iy, maskSigma);
            float4 sampleColor = shaderTexture.Sample(samplerState, tex + float2(ix * texelWidth * 4.5f, iy * texelHeight * 4.5f));
            textAlphaSum += sampleColor.a * w;
            
            // 如果遇到有文字存在的像素，就记录颜色
            if (sampleColor.a > 0.05f) {
                textColorSum += (sampleColor.rgb / sampleColor.a) * w;
                textHitWeightSum += w;
            }
            maskWeightSum += w;
        }
    }
    
    float rawMask = textAlphaSum / maskWeightSum;
    
    // 【模糊区域遮罩】：下限再次调低到令人发指的 0.0005，只要探测到万分之一的文字浓度也会立刻开启模糊！
    // 保证了整块背景会形成连绵成片的极广阔的高斯模糊地带！
    float blurMask = smoothstep(0.0005f, 0.02f, rawMask);
    
    // 【描边底色遮罩】：保留紧贴文字本体的效果
    float strokeMask = smoothstep(0.005f, 0.06f, rawMask);
    
    // 3. 字体动态发光/描边底色 (Dynamic Base Stroke)
    float3 avgTextColor = float3(1, 1, 1);
    if (textHitWeightSum > 0.001f) {
        avgTextColor = textColorSum / textHitWeightSum;
    }
    
    // 计算文字亮度的经典公式
    float brightness = dot(avgTextColor, float3(0.299f, 0.587f, 0.114f));
    
    // 如果文字比较亮（如白色），加黑色底边；否则加白色底边
    float3 tintColor = brightness > 0.5f ? float3(0.0f, 0.0f, 0.0f) : float3(1.0f, 1.0f, 1.0f);
    
    // 向已经高度模糊的背景上，仅仅在“描边边缘(strokeMask)”处混入黑/白底色
    float tintStrength = 0.55f;
    float4 baseBackground = bgBlurred;
    baseBackground.rgb = lerp(baseBackground.rgb, tintColor, strokeMask * tintStrength);
    
    // 最终融合：有文字一大块背景(blurMask)显示模糊图(baseBackground)，没文字显示原图(bgNormal)
    float4 finalBg = lerp(bgNormal, baseBackground, blurMask);
    
    // 将终极文字(termColor)清晰地贴在最上方！
    float3 finalColor = termColor.rgb + finalBg.rgb * (1.0f - termColor.a);
    
    return float4(finalColor, 1.0f);
}
