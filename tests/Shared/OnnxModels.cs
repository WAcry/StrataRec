using System.Buffers.Binary;
using System.Text;

namespace StrataRec.Testing;

/// <summary>
/// ONNX models for the tests and the sample, encoded here as <c>ModelProto</c> bytes (onnx.proto, IR version 8, opset
/// 13), so they are reproducible without Python or the onnx package. The sample's
/// <c>samples/home_feed/models/ctr_model.onnx</c> is <see cref="CtrModel"/>; a test checks the committed file is exactly
/// what this writes (regenerate it with <see cref="WriteSample"/>).
/// </summary>
internal static class OnnxModels
{
    /// <summary><c>TensorProto.DataType</c> values.</summary>
    public const int Float = 1;
    public const int Int64 = 7;
    public const int String = 8;
    public const int Double = 11;

    /// <summary>The sample's path, relative to the repository root.</summary>
    public const string SamplePath = "samples/home_feed/models/ctr_model.onnx";

    /// <summary>
    /// The sample's ctr model, the network form of its <c>linear@1</c> binding: <c>p_click = sigmoid(-1.5 +
    /// 12·smoothed_ctr + 0.05·log_followers)</c> over float32 <c>[N]</c> inputs.
    /// </summary>
    public static byte[] CtrModel() => Linear("ctr_model", "p_click", -1.5f, [("smoothed_ctr", 12f), ("log_followers", 0.05f)], sigmoid: true);

    /// <summary>
    /// <c>output = activation(bias + Σ weight·input)</c> over <c>[N]</c> inputs of <paramref name="inputType"/> (cast to
    /// float32 in the graph when they are not), with a float32 <c>[N]</c> output.
    /// </summary>
    public static byte[] Linear(string graphName, string output, float bias, (string Input, float Weight)[] weights, bool sigmoid, int inputType = Float)
    {
        var graph = new Proto();
        var nodes = 0;
        string? sum = null;
        foreach (var (input, weight) in weights)
        {
            var value = input;
            if (inputType != Float)
            {
                value = input + "_f";
                graph.Message(1, Node($"cast_{nodes++}", "Cast", [input], [value], ("to", Float)));
            }

            var term = "w_" + input;
            graph.Message(5, Scalar(term, weight));
            graph.Message(1, Node($"mul_{nodes++}", "Mul", [value, term], [term + "_x"]));
            if (sum is null)
            {
                sum = term + "_x";
            }
            else
            {
                graph.Message(1, Node($"add_{nodes++}", "Add", [sum, term + "_x"], ["sum_" + nodes]));
                sum = "sum_" + nodes;
            }
        }

        graph.Message(5, Scalar("bias", bias));
        var logit = sigmoid ? "logit" : output;
        graph.Message(1, Node($"add_{nodes++}", "Add", [sum!, "bias"], [logit]));
        if (sigmoid)
        {
            graph.Message(1, Node($"sigmoid_{nodes++}", "Sigmoid", [logit], [output]));
        }

        graph.String(2, graphName);
        foreach (var (input, _) in weights)
        {
            graph.Message(11, Value(input, inputType));
        }

        graph.Message(12, Value(output, Float));
        return Model(graph);
    }

    /// <summary>A model whose only input is a tensor of <paramref name="inputType"/>, cast to its float32 output.</summary>
    public static byte[] Cast(string input, int inputType, string output)
    {
        var graph = new Proto()
            .Message(1, Node("cast", "Cast", [input], [output], ("to", Float)))
            .String(2, "cast")
            .Message(11, Value(input, inputType))
            .Message(12, Value(output, Float));
        return Model(graph);
    }

    /// <summary>Writes <see cref="CtrModel"/> to <see cref="SamplePath"/> under <paramref name="repositoryRoot"/>.</summary>
    public static void WriteSample(string repositoryRoot) => File.WriteAllBytes(Path.Combine(repositoryRoot, SamplePath), CtrModel());

    private static byte[] Model(Proto graph) => new Proto()
        .Varint(1, 8) // ir_version
        .String(2, "StrataRec tests/Shared/OnnxModels.cs") // producer_name
        .Message(7, graph)
        .Message(8, new Proto().String(1, string.Empty).Varint(2, 13)) // opset_import: the default domain, opset 13
        .ToArray();

    private static Proto Node(string name, string op, string[] inputs, string[] outputs, (string Name, long Value)? attribute = null)
    {
        var node = new Proto();
        foreach (var input in inputs)
        {
            node.String(1, input);
        }

        foreach (var output in outputs)
        {
            node.String(2, output);
        }

        node.String(3, name).String(4, op);
        if (attribute is { } a)
        {
            node.Message(5, new Proto().String(1, a.Name).Varint(3, a.Value).Varint(20, 2)); // AttributeProto: name, i, type INT
        }

        return node;
    }

    /// <summary>A float32 scalar initializer (<c>TensorProto</c> without dims).</summary>
    private static Proto Scalar(string name, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return new Proto().Varint(2, Float).String(8, name).Bytes(9, bytes); // data_type, name, raw_data
    }

    /// <summary>A <c>ValueInfoProto</c> of a tensor with one dynamic dimension <c>N</c>.</summary>
    private static Proto Value(string name, int elementType)
    {
        var shape = new Proto().Message(1, new Proto().String(2, "N")); // dim { dim_param: "N" }
        var tensor = new Proto().Varint(1, elementType).Message(2, shape);
        return new Proto().String(1, name).Message(2, new Proto().Message(1, tensor)); // name, type { tensor_type }
    }

    /// <summary>The protobuf wire format, as much of it as these messages use.</summary>
    private sealed class Proto
    {
        private readonly MemoryStream _bytes = new();

        public Proto Varint(int field, long value)
        {
            Key(field, 0);
            WriteVarint((ulong)value);
            return this;
        }

        public Proto String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Proto Bytes(int field, byte[] value)
        {
            Key(field, 2);
            WriteVarint((ulong)value.Length);
            _bytes.Write(value);
            return this;
        }

        public Proto Message(int field, Proto message) => Bytes(field, message.ToArray());

        public byte[] ToArray() => _bytes.ToArray();

        private void Key(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            _bytes.WriteByte((byte)value);
        }
    }
}
