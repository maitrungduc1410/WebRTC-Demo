"""Finishes a tf2onnx conversion so plain ONNX Runtime (CPU, DirectML, Windows ML) can run it.

* Pins every symbolic dimension to 1 (the app always runs batch 1), so execution providers get
  static shapes.
* Replaces MediaPipe's custom TFL_Convolution2DTransposeBias op, which tf2onnx keeps as an unknown
  custom node (only the selfie segmenter has one). There it is a 2x2 kernel, stride 2, SAME padding
  (no padding when stride == kernel) transposed convolution with a bias: exactly ONNX ConvTranspose.
  Its input is a Transpose (NCHW -> NHWC) of an NCHW tensor, so the replacement reads that tensor
  directly and transposes the 1-channel result back to NHWC.
* Renames initializers by first use so the output is byte-reproducible.

Usage: python postprocess.py in.onnx out.onnx
"""
import sys

import numpy as np
import onnx
from onnx import helper, numpy_helper

CUSTOM = "TFL_Convolution2DTransposeBias"


def pin_dimensions(graph: onnx.GraphProto) -> None:
    for value in list(graph.input) + list(graph.output) + list(graph.value_info):
        for dim in value.type.tensor_type.shape.dim:
            if not dim.HasField("dim_value"):
                dim.Clear()
                dim.dim_value = 1


def replace_transpose_conv(model: onnx.ModelProto) -> None:
    graph = model.graph
    nodes = [n for n in graph.node if n.op_type == CUSTOM]
    if not nodes:
        return
    if len(nodes) != 1:
        raise SystemExit(f"expected at most one {CUSTOM} node, found {len(nodes)}")
    node = nodes[0]
    data, weights_name, bias_name = node.input
    producers = {o: n for n in graph.node for o in n.output}
    transpose = producers[data]
    perm = next(a.ints for a in transpose.attribute if a.name == "perm")
    if transpose.op_type != "Transpose" or list(perm) != [0, 2, 3, 1]:
        raise SystemExit("custom op input is not an NCHW -> NHWC transpose")
    initializers = {i.name: i for i in graph.initializer}
    weights = numpy_helper.to_array(initializers[weights_name])  # OHWI
    bias = numpy_helper.to_array(initializers[bias_name])
    if weights.shape[1:3] != (2, 2):
        raise SystemExit(f"unexpected kernel {weights.shape}")

    # ONNX ConvTranspose wants (in_channels, out_channels, kH, kW).
    w = np.ascontiguousarray(weights.transpose(3, 0, 1, 2)).astype(np.float32)
    graph.initializer.append(numpy_helper.from_array(w, "segment_deconv_W"))
    graph.initializer.append(numpy_helper.from_array(bias.astype(np.float32), "segment_deconv_B"))
    output = node.output[0]
    index = list(graph.node).index(node)
    graph.node.remove(node)
    graph.node.insert(index, helper.make_node(
        "Transpose", ["segment_nchw"], [output], name="segment_to_nhwc", perm=[0, 2, 3, 1]))
    graph.node.insert(index, helper.make_node(
        "ConvTranspose", [transpose.input[0], "segment_deconv_W", "segment_deconv_B"], ["segment_nchw"],
        name="segment_deconv", kernel_shape=[2, 2], strides=[2, 2], pads=[0, 0, 0, 0]))

    if not any(data in n.input for n in graph.node):
        graph.node.remove(transpose)
    for name in (weights_name, bias_name):
        if not any(name in n.input for n in graph.node):
            graph.initializer.remove(initializers[name])
    for domain in list(model.opset_import):
        if domain.domain and not any(n.domain == domain.domain for n in graph.node):
            model.opset_import.remove(domain)


def canonical_initializers(graph: onnx.GraphProto) -> None:
    """tf2onnx numbers folded constants in object-address order, which changes run to run; renames
    and orders initializers by first use so the file is byte-reproducible."""
    by_name = {i.name: i for i in graph.initializer}
    order: list[str] = []
    for node in graph.node:
        order.extend(name for name in node.input if name in by_name and name not in order)
    order.extend(name for name in by_name if name not in order)
    names = {old: f"const_{index}" for index, old in enumerate(order)}
    for node in graph.node:
        for index, name in enumerate(node.input):
            if name in names:
                node.input[index] = names[name]
    initializers = [by_name[name] for name in order]
    for initializer in initializers:
        initializer.name = names[initializer.name]
    del graph.initializer[:]
    graph.initializer.extend(initializers)


def main(source: str, target: str) -> None:
    model = onnx.load(source)
    replace_transpose_conv(model)
    pin_dimensions(model.graph)
    canonical_initializers(model.graph)
    model.producer_name = "tf2onnx+postprocess.py"
    model.doc_string = ""
    model.graph.doc_string = ""  # tf2onnx writes the (temporary) source path here
    onnx.checker.check_model(model, full_check=True)
    onnx.save(model, target)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
