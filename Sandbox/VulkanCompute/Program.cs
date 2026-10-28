// VulkanCompute: skill recipe 7.12 (references/vulkan.md). Squares 1024 indices on the GPU and checks every value.
// Usage: dotnet run -c Release --project Sandbox/VulkanCompute [-- <gpu index>]
// Exit codes: 0 = all values correct, 1 = wrong values, 2 = no usable GPU / Vulkan error.
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

const int Count = 1024;

try
{
    return Run(args.Length > 0 ? int.Parse(args[0]) : -1);
}
catch (VkResultError e)
{
    Console.WriteLine($"Vulkan error: {e.Result}");
    return 2;
}

static byte[] CompileShader()
{
    using var compiler = ShaderC.CreateCompiler();
    using var options = ShaderC.CreateCompilerOptions();
    options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
    var glsl = """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, binding = 0) buffer Data { uint values[]; };
        void main() { uint i = gl_GlobalInvocationID.x; values[i] = i * i; }
        """u8;
    using var result = compiler.Compile(glsl, ShaderKind.ComputeShader, "square.comp"u8, "main"u8, options);
    unsafe
    {
        return new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes).ToArray();
    }
}

static unsafe int Run(int gpuIndex)
{
    var spirvBytes = CompileShader();
    using var instance = Vulkan.CreateInstance(
        new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = VK.API_VERSION_1_3 },
            EnabledExtensions = [],
            EnabledLayers = [],
        }
    );
    var devices = instance.PhysicalDevices;
    if (devices.Length == 0)
    {
        Console.WriteLine("No Vulkan physical device.");
        return 2;
    }

    // Prefer a discrete GPU unless an index was given (7.11: choose by type, not by index).
    var physical = devices[0];
    if (gpuIndex >= 0)
        physical = devices[gpuIndex];
    else
        foreach (var pd in devices)
            if (pd.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu)
            {
                physical = pd;
                break;
            }
    var props = physical.GetProperties();
    Console.WriteLine($"GPU: {System.Text.Encoding.UTF8.GetString(props.DeviceName, 256).TrimEnd('\0')}");

    var family = physical.FindQueueFamily(VkQueueFlags.ComputeBit);
    var priority = 1f;
    var queueInfo = new VkDeviceQueueCreateInfo
    {
        QueueFamilyIndex = family,
        QueueCount = 1,
        QueuePriorities = &priority,
    };
    using var device = physical.CreateDevice(new DeviceEx.CreateInfo { QueueCreateInfos = [queueInfo] });
    var queue = device.GetQueue(family, 0);
    using var buffer = device.CreateBuffer(
        new BufferEx.CreateInfo
        {
            Name = "values",
            BufferSize = ByteSize.Of<uint>(Count),
            BufferUsage = VkBufferUsageFlags.StorageBufferBit,
            AllocPreference = MemoryPreference.PreferCpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        }
    );

    var module = ShaderModuleExtensions.FromSpirv(device, spirvBytes, null);
    var binding = new VkDescriptorSetLayoutBinding
    {
        Binding = 0,
        DescriptorType = VkDescriptorType.StorageBuffer,
        DescriptorCount = 1,
        StageFlags = VkShaderStageFlags.ComputeBit,
    };
    var setLayout = device.CreateDescriptorSetLayout(
        new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding },
        null
    );
    var pipelineLayout = device.CreatePipelineLayout([setLayout], [], null);
    Span<VkPipeline> pipelines = stackalloc VkPipeline[1]; // output array (T3)
    fixed (byte* entry = "main"u8)
        device.CreateComputePipelines(
            default,
            [
                new VkComputePipelineCreateInfo
                {
                    Stage = new VkPipelineShaderStageCreateInfo
                    {
                        Stage = VkShaderStageFlags.ComputeBit,
                        Module = module,
                        Name = entry,
                    },
                    Layout = pipelineLayout,
                },
            ],
            null,
            pipelines
        );

    var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1 };
    var descriptorPool = device.CreateDescriptorPool(
        new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize },
        null
    );
    Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
    device.AllocateDescriptorSets(
        new VkDescriptorSetAllocateInfo
        {
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            SetLayouts = &setLayout,
        },
        sets
    );
    var bufferInfo = new VkDescriptorBufferInfo
    {
        Buffer = buffer.VkBuffer,
        Offset = default,
        Range = buffer.BufferSize,
    };
    device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet
            {
                DstSet = sets[0],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &bufferInfo,
            },
        ],
        []
    );

    var commandPool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
    var cmd = device.AllocateCommandBuffer(
        new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary }
    );
    cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
    cmd.BindPipeline(VkPipelineBindPoint.Compute, pipelines[0]);
    cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, pipelineLayout, 0, sets, []);
    cmd.Dispatch(Count / 64, 1, 1);
    var barrier = new VkMemoryBarrier
    {
        SrcAccessMask = VkAccessFlags.ShaderWriteBit,
        DstAccessMask = VkAccessFlags.HostReadBit,
    };
    cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.HostBit, default, [barrier], [], []);
    cmd.End();

    var fence = device.CreateFence(default, null);
    queue.Submit([], [], [cmd], [], fence);
    var waited = device.WaitForFence(fence, nint.MaxValue);

    var errors = 0;
    using (var mapped = buffer.Map())
    {
        Span<uint> values = mapped.AsSpan<uint>();
        for (var i = 0; i < Count; i++)
            if (values[i] != (uint)(i * i))
                errors++;
        Console.WriteLine($"Fence: {waited}; values[{Count - 1}] = {values[Count - 1]}; errors = {errors}");
    }

    device.DestroyFence(fence, null);
    device.DestroyCommandPool(commandPool, null);
    device.DestroyDescriptorPool(descriptorPool, null);
    device.DestroyPipeline(pipelines[0], null);
    device.DestroyPipelineLayout(pipelineLayout, null);
    device.DestroyDescriptorSetLayout(setLayout, null);
    device.DestroyShaderModule(module, null);
    return errors == 0 ? 0 : 1;
}
