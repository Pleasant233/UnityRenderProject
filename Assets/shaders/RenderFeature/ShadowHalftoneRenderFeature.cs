using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class ShadowHalftoneRenderFeature : ScriptableRendererFeature
{
    public enum CoordinateSpace
    {
        Screen,
        World
    }

    public enum DebugView
    {
        None,
        Luminance,
        ShadowMask,
        Pattern,
        SourceColor
    }

    [Serializable]
    public class ShadowHalftoneSettings
    {
        public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public DebugView debugView = DebugView.None;
        public CoordinateSpace coordinateSpace = CoordinateSpace.Screen;
        public Shader halftoneShader;
        public Color patternColor = Color.black;
        [Range(0.0f, 1.0f)] public float luminanceThreshold = 0.45f;
        [Range(0.001f, 0.5f)] public float thresholdSoftness = 0.08f;
        [Range(0.0f, 96.0f)] public float cellSize = 18.0f;
        [Min(0.001f)] public float worldCellSize = 0.25f;
        [Range(0.05f, 0.95f)] public float dotRadius = 0.35f;
        [Range(0.0f, 0.2f)] public float dotSoftness = 0.04f;
        [Range(0.0f, 0.2f)] public float gridLineWidth = 0.0f;
        [Range(-180.0f, 180.0f)] public float rotation = 15.0f;
        [Range(0.0f, 1.0f)] public float intensity = 1.0f;
    }

    class ShadowHalftonePass : ScriptableRenderPass
    {
        const string CopyPassName = "Shadow Halftone Copy Color";
        const string HalftonePassName = "Shadow Halftone";

        static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        static readonly int SourceTextureTexelSizeId = Shader.PropertyToID("_SourceTexture_TexelSize");
        static readonly int PatternColorId = Shader.PropertyToID("_PatternColor");
        static readonly int HalftoneParamsId = Shader.PropertyToID("_HalftoneParams");
        static readonly int PatternParamsId = Shader.PropertyToID("_PatternParams");
        static readonly int DebugViewId = Shader.PropertyToID("_DebugView");
        static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int UseWorldSpacePatternId = Shader.PropertyToID("_UseWorldSpacePattern");

        readonly ProfilingSampler m_ProfilingSampler = new(HalftonePassName);
        readonly ShadowHalftoneSettings m_Settings;
        Material m_Material;
        RTHandle m_CopiedColor;

        public ShadowHalftonePass(Material material, ShadowHalftoneSettings settings)
        {
            m_Material = material;
            m_Settings = settings;
            requiresIntermediateTexture = true;
            UpdateInputRequirements();
        }

        public void SetMaterial(Material material)
        {
            m_Material = material;
        }

        public void UpdateInputRequirements()
        {
            ConfigureInput(m_Settings.coordinateSpace == CoordinateSpace.World
                ? ScriptableRenderPassInput.Depth
                : ScriptableRenderPassInput.None);
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
            bool useWorldSpace = m_Settings.coordinateSpace == CoordinateSpace.World && cameraDepth.IsValid();

            var copiedColorDesc = renderGraph.GetTextureDesc(cameraColor);
            copiedColorDesc.name = "_ShadowHalftoneColorCopy";
            copiedColorDesc.clearBuffer = false;
            TextureHandle copiedColor = renderGraph.CreateTexture(copiedColorDesc);

            renderGraph.AddBlitPass(cameraColor, copiedColor, Vector2.one, Vector2.zero, passName: CopyPassName);

            var halftoneColorDesc = renderGraph.GetTextureDesc(cameraColor);
            halftoneColorDesc.name = "_ShadowHalftoneColor";
            halftoneColorDesc.clearBuffer = false;
            TextureHandle halftoneColor = renderGraph.CreateTexture(halftoneColorDesc);

            using (var builder = renderGraph.AddRasterRenderPass<ShadowHalftonePassData>(HalftonePassName, out var passData, m_ProfilingSampler))
            {
                passData.source = copiedColor;
                passData.material = m_Material;
                passData.patternColor = m_Settings.patternColor;
                passData.sourceTexelSize = new Vector4(
                    1.0f / Mathf.Max(1, copiedColorDesc.width),
                    1.0f / Mathf.Max(1, copiedColorDesc.height),
                    copiedColorDesc.width,
                    copiedColorDesc.height);
                passData.debugView = (int)m_Settings.debugView;
                passData.depth = cameraDepth;
                passData.useWorldSpace = useWorldSpace;
                FillParams(passData.useWorldSpace, out passData.halftoneParams, out passData.patternParams);

                builder.UseTexture(passData.source, AccessFlags.Read);
                if (useWorldSpace)
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                builder.SetRenderAttachment(halftoneColor, 0, AccessFlags.Write);
                builder.SetRenderFunc((ShadowHalftonePassData data, RasterGraphContext context) =>
                {
                    data.material.SetTexture(SourceTextureId, data.source);
                    data.material.SetVector(SourceTextureTexelSizeId, data.sourceTexelSize);
                    data.material.SetColor(PatternColorId, data.patternColor);
                    data.material.SetVector(HalftoneParamsId, data.halftoneParams);
                    data.material.SetVector(PatternParamsId, data.patternParams);
                    data.material.SetFloat(DebugViewId, data.debugView);
                    data.material.SetFloat(UseWorldSpacePatternId, data.useWorldSpace ? 1.0f : 0.0f);
                    if (data.useWorldSpace && data.depth.IsValid())
                        data.material.SetTexture(CameraDepthTextureId, data.depth);

                    context.cmd.DrawProcedural(
                        Matrix4x4.identity,
                        data.material,
                        0,
                        MeshTopology.Triangles,
                        3);
                });
            }

            resourceData.cameraColor = halftoneColor;
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
                name: "_ShadowHalftoneColorCopy");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_Material == null || m_CopiedColor == null)
                return;

            if (renderingData.cameraData.cameraType == CameraType.Preview)
                return;

            RTHandle cameraColor = renderingData.cameraData.renderer.cameraColorTargetHandle;
            CommandBuffer cmd = CommandBufferPool.Get(HalftonePassName);

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                SetMaterialProperties();
                Blitter.BlitCameraTexture(cmd, cameraColor, m_CopiedColor);
                m_Material.SetTexture(SourceTextureId, m_CopiedColor);
                m_Material.SetVector(SourceTextureTexelSizeId, new Vector4(
                    1.0f / Mathf.Max(1, m_CopiedColor.rt.width),
                    1.0f / Mathf.Max(1, m_CopiedColor.rt.height),
                    m_CopiedColor.rt.width,
                    m_CopiedColor.rt.height));
                if (m_Settings.coordinateSpace == CoordinateSpace.World)
                    m_Material.SetTexture(CameraDepthTextureId, renderingData.cameraData.renderer.cameraDepthTargetHandle);
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
            bool useWorldSpace = m_Settings.coordinateSpace == CoordinateSpace.World;
            FillParams(useWorldSpace, out Vector4 halftoneParams, out Vector4 patternParams);
            m_Material.SetColor(PatternColorId, m_Settings.patternColor);
            m_Material.SetVector(HalftoneParamsId, halftoneParams);
            m_Material.SetVector(PatternParamsId, patternParams);
            m_Material.SetFloat(DebugViewId, (int)m_Settings.debugView);
            m_Material.SetFloat(UseWorldSpacePatternId, useWorldSpace ? 1.0f : 0.0f);
        }

        void FillParams(bool useWorldSpace, out Vector4 halftoneParams, out Vector4 patternParams)
        {
            halftoneParams = new Vector4(
                Mathf.Clamp01(m_Settings.luminanceThreshold),
                Mathf.Max(0.001f, m_Settings.thresholdSoftness),
                Mathf.Clamp01(m_Settings.intensity),
                m_Settings.rotation * Mathf.Deg2Rad);

            float cellSize = useWorldSpace
                ? Mathf.Max(0.001f, m_Settings.worldCellSize)
                : Mathf.Max(1.0f, m_Settings.cellSize);

            patternParams = new Vector4(
                cellSize,
                Mathf.Clamp01(m_Settings.dotRadius),
                Mathf.Clamp01(m_Settings.dotSoftness),
                Mathf.Clamp01(m_Settings.gridLineWidth));
        }

        class ShadowHalftonePassData
        {
            public TextureHandle source;
            public TextureHandle depth;
            public Material material;
            public Color patternColor;
            public Vector4 sourceTexelSize;
            public Vector4 halftoneParams;
            public Vector4 patternParams;
            public int debugView;
            public bool useWorldSpace;
        }
    }

    [SerializeField] ShadowHalftoneSettings settings = new();

    Material m_Material;
    ShadowHalftonePass m_Pass;

    public override void Create()
    {
        CreateMaterialIfNeeded();

        m_Pass = new ShadowHalftonePass(m_Material, settings)
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
        m_Pass.UpdateInputRequirements();
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

        Shader shader = settings.halftoneShader != null ? settings.halftoneShader : Shader.Find("Hidden/XYXS/ShadowHalftone");
        if (shader != null)
            m_Material = CoreUtils.CreateEngineMaterial(shader);
    }
}
