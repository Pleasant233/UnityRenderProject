using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class DepthNormalOutlineRenderFeature : ScriptableRendererFeature
{
    public enum DebugView
    {
        None,
        SolidColor,
        EdgeMask,
        Depth,
        Normals,
        SourceColor
    }

    [Serializable]
    public class OutlineSettings
    {
        public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public DebugView debugView = DebugView.None;
        public Shader outlineShader;
        public Color outlineColor = Color.black;
        [Range(0.0f, 1.0f)] public float intensity = 1.0f;
        [Range(0.5f, 6.0f)] public float thickness = 1.0f;
        [Range(0.0f, 10.0f)] public float depthSensitivity = 2.0f;
        [Range(0.0f, 10.0f)] public float normalSensitivity = 1.5f;
        [Range(0.001f, 1.0f)] public float depthThreshold = 0.03f;
        [Range(0.001f, 1.0f)] public float normalThreshold = 0.2f;
        [Range(0.001f, 1.0f)] public float edgeSoftness = 0.08f;
        [Range(0.0f, 1.0f)] public float depthFade = 0.0f;
        [Min(0.0f)] public float distanceFadeStart = 10.0f;
        [Min(0.01f)] public float distanceFadeEnd = 30.0f;
    }

    class OutlinePass : ScriptableRenderPass
    {
        const string CopyPassName = "Depth Normal Outline Copy Color";
        const string OutlinePassName = "Depth Normal Outline";

        static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
        static readonly int OutlineParamsId = Shader.PropertyToID("_OutlineParams");
        static readonly int OutlineThresholdsId = Shader.PropertyToID("_OutlineThresholds");
        static readonly int DistanceFadeParamsId = Shader.PropertyToID("_DistanceFadeParams");
        static readonly int DebugViewId = Shader.PropertyToID("_DebugView");
        static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int CameraNormalsTextureId = Shader.PropertyToID("_CameraNormalsTexture");
        static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        static readonly int SourceTextureTexelSizeId = Shader.PropertyToID("_SourceTexture_TexelSize");

        readonly ProfilingSampler m_ProfilingSampler = new(OutlinePassName);
        Material m_Material;
        readonly OutlineSettings m_Settings;
        RTHandle m_CopiedColor;

        public OutlinePass(Material material, OutlineSettings settings)
        {
            m_Material = material;
            m_Settings = settings;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            requiresIntermediateTexture = true;
        }

        public void SetMaterial(Material material)
        {
            m_Material = material;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.activeColorTexture.IsValid())
                return;

            TextureHandle cameraColor = resourceData.activeColorTexture;
            TextureHandle cameraDepth = resourceData.cameraDepthTexture;
            TextureHandle cameraNormals = resourceData.cameraNormalsTexture;

            if (!cameraDepth.IsValid() && m_Settings.debugView != DebugView.SolidColor && m_Settings.debugView != DebugView.SourceColor)
                return;

            var copiedColorDesc = renderGraph.GetTextureDesc(cameraColor);
            copiedColorDesc.name = "_DepthNormalOutlineColorCopy";
            copiedColorDesc.clearBuffer = false;
            TextureHandle copiedColor = renderGraph.CreateTexture(copiedColorDesc);

            renderGraph.AddBlitPass(cameraColor, copiedColor, Vector2.one, Vector2.zero, passName: CopyPassName);

            var outlinedColorDesc = renderGraph.GetTextureDesc(cameraColor);
            outlinedColorDesc.name = "_DepthNormalOutlineColor";
            outlinedColorDesc.clearBuffer = false;
            TextureHandle outlinedColor = renderGraph.CreateTexture(outlinedColorDesc);

            using (var builder = renderGraph.AddRasterRenderPass<OutlinePassData>(OutlinePassName, out var passData, m_ProfilingSampler))
            {
                passData.source = copiedColor;
                passData.depth = cameraDepth;
                passData.normals = cameraNormals;
                passData.material = m_Material;
                passData.outlineColor = m_Settings.outlineColor;
                passData.sourceTexelSize = new Vector4(
                    1.0f / Mathf.Max(1, copiedColorDesc.width),
                    1.0f / Mathf.Max(1, copiedColorDesc.height),
                    copiedColorDesc.width,
                    copiedColorDesc.height);
                passData.outlineParams = new Vector4(
                    Mathf.Max(0.5f, m_Settings.thickness),
                    Mathf.Max(0.0f, m_Settings.depthSensitivity),
                    Mathf.Max(0.0f, m_Settings.normalSensitivity),
                    Mathf.Clamp01(m_Settings.intensity));
                passData.outlineThresholds = new Vector4(
                    Mathf.Max(0.001f, m_Settings.depthThreshold),
                    Mathf.Max(0.001f, m_Settings.normalThreshold),
                    Mathf.Max(0.001f, m_Settings.edgeSoftness),
                    Mathf.Clamp01(m_Settings.depthFade));
                float fadeStart = Mathf.Max(0.0f, m_Settings.distanceFadeStart);
                float fadeEnd = Mathf.Max(fadeStart + 0.01f, m_Settings.distanceFadeEnd);
                passData.distanceFadeParams = new Vector4(fadeStart, fadeEnd, 0.0f, 0.0f);
                passData.debugView = (int)m_Settings.debugView;

                builder.UseTexture(passData.source, AccessFlags.Read);
                if (passData.depth.IsValid())
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                if (passData.normals.IsValid())
                    builder.UseTexture(passData.normals, AccessFlags.Read);

                builder.SetRenderAttachment(outlinedColor, 0, AccessFlags.Write);
                builder.SetRenderFunc((OutlinePassData data, RasterGraphContext context) =>
                {
                    data.material.SetColor(OutlineColorId, data.outlineColor);
                    data.material.SetVector(OutlineParamsId, data.outlineParams);
                    data.material.SetVector(OutlineThresholdsId, data.outlineThresholds);
                    data.material.SetVector(DistanceFadeParamsId, data.distanceFadeParams);
                    data.material.SetFloat(DebugViewId, data.debugView);
                    data.material.SetTexture(SourceTextureId, data.source);
                    data.material.SetVector(SourceTextureTexelSizeId, data.sourceTexelSize);
                    if (data.depth.IsValid())
                        data.material.SetTexture(CameraDepthTextureId, data.depth);
                    if (data.normals.IsValid())
                        data.material.SetTexture(CameraNormalsTextureId, data.normals);

                    context.cmd.DrawProcedural(
                        Matrix4x4.identity,
                        data.material,
                        0,
                        MeshTopology.Triangles,
                        3);
                });
            }

            resourceData.cameraColor = outlinedColor;
        }

#pragma warning disable 618, 672
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.msaaSamples = 1;
            descriptor.depthBufferBits = 0;
            descriptor.depthStencilFormat = GraphicsFormat.None;

            RenderingUtils.ReAllocateHandleIfNeeded(
                ref m_CopiedColor,
                descriptor,
                FilterMode.Bilinear,
                TextureWrapMode.Clamp,
                name: "_DepthNormalOutlineColorCopy");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_Material == null || m_CopiedColor == null)
                return;

            if (renderingData.cameraData.cameraType == CameraType.Preview)
                return;

            RTHandle cameraColor = renderingData.cameraData.renderer.cameraColorTargetHandle;
            CommandBuffer cmd = CommandBufferPool.Get(OutlinePassName);

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                SetMaterialProperties();
                Blitter.BlitCameraTexture(cmd, cameraColor, m_CopiedColor);
                Blitter.BlitCameraTexture(cmd, m_CopiedColor, cameraColor, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, m_Material, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore 618, 672

        public void Dispose()
        {
            m_CopiedColor?.Release();
        }

        void SetMaterialProperties()
        {
            m_Material.SetColor(OutlineColorId, m_Settings.outlineColor);
            m_Material.SetVector(OutlineParamsId, new Vector4(
                Mathf.Max(0.5f, m_Settings.thickness),
                Mathf.Max(0.0f, m_Settings.depthSensitivity),
                Mathf.Max(0.0f, m_Settings.normalSensitivity),
                Mathf.Clamp01(m_Settings.intensity)));
            m_Material.SetVector(OutlineThresholdsId, new Vector4(
                Mathf.Max(0.001f, m_Settings.depthThreshold),
                Mathf.Max(0.001f, m_Settings.normalThreshold),
                Mathf.Max(0.001f, m_Settings.edgeSoftness),
                Mathf.Clamp01(m_Settings.depthFade)));
            float fadeStart = Mathf.Max(0.0f, m_Settings.distanceFadeStart);
            float fadeEnd = Mathf.Max(fadeStart + 0.01f, m_Settings.distanceFadeEnd);
            m_Material.SetVector(DistanceFadeParamsId, new Vector4(fadeStart, fadeEnd, 0.0f, 0.0f));
            m_Material.SetFloat(DebugViewId, (int)m_Settings.debugView);
        }

        class OutlinePassData
        {
            public TextureHandle source;
            public TextureHandle depth;
            public TextureHandle normals;
            public Material material;
            public Color outlineColor;
            public Vector4 sourceTexelSize;
            public Vector4 outlineParams;
            public Vector4 outlineThresholds;
            public Vector4 distanceFadeParams;
            public int debugView;
        }
    }

    [SerializeField] OutlineSettings settings = new();

    Material m_Material;
    OutlinePass m_Pass;

    public override void Create()
    {
        CreateMaterialIfNeeded();

        m_Pass = new OutlinePass(m_Material, settings)
        {
            renderPassEvent = settings.passEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CreateMaterialIfNeeded();

        if (renderingData.cameraData.cameraType == CameraType.Preview || m_Pass == null || m_Material == null)
            return;

        m_Pass.SetMaterial(m_Material);
        m_Pass.renderPassEvent = settings.passEvent;
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        CoreUtils.Destroy(m_Material);
    }

    void CreateMaterialIfNeeded()
    {
        if (m_Material != null)
            return;

        Shader shader = settings.outlineShader != null ? settings.outlineShader : Shader.Find("Hidden/XYXS/DepthNormalOutline");
        if (shader != null)
            m_Material = CoreUtils.CreateEngineMaterial(shader);
    }
}
