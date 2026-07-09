#ifndef BASE_TOOL_FUNCTION_INCLUDED
#define BASE_TOOL_FUNCTION_INCLUDED

float drawCircle(float2 center, float radius)
{
    float circle = length(center) - radius;
    return circle;
}

float drawBox(float2 center, float2 halfSize)
{
    float2 q = abs(center) - halfSize;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
}

float drawSquare(float2 center, float halfSize)
{
    return drawBox(center, float2(halfSize, halfSize));
}

float drawRectangle(float2 center, float2 halfSize)
{
    return drawBox(center, halfSize);
}

float drawTriangle(float2 center, float radius)
{
    const float sqrt3 = 1.7320508;
    float2 p = center;

    p.x = abs(p.x) - radius;
    p.y = p.y + radius / sqrt3;

    if (p.x + sqrt3 * p.y > 0.0)
    {
        p = float2(p.x - sqrt3 * p.y, -sqrt3 * p.x - p.y) * 0.5;
    }

    p.x -= clamp(p.x, -2.0 * radius, 0.0);
    return -length(p) * sign(p.y);
}

float drawShape(float2 center, float shape, float radius, float2 rectangleHalfSize)
{
    if (shape < 0.5)
    {
        return drawCircle(center, radius);
    }

    if (shape < 1.5)
    {
        return drawSquare(center, radius);
    }

    if (shape < 2.5)
    {
        return drawRectangle(center, rectangleHalfSize);
    }

    return drawTriangle(center, radius);
}

float smoothUnion(float d1, float d2, float k)
{
    float h = saturate(0.5 + 0.5 * (d2 - d1) / k);
    return lerp(d2, d1, h) - k * h * (1.0 - h);
}

float hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float noise(float2 p)
{
    float2 id = floor(p);
    float2 f = frac(p);

    float a = hash21(id);
    float b = hash21(id + float2(1, 0));
    float c = hash21(id + float2(0, 1));
    float d = hash21(id + float2(1, 1));
    float2 u = f * f * (3.0 - 2.0 * f);

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

#endif
