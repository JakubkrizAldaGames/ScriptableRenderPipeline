using UnityEngine;
using UnityEngine.Experimental.VFX;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor.Experimental;

namespace UnityEditor.Experimental
{
    public class ShaderSourceBuilder
    {
        public void WriteFormat(string str, object arg0)                                { m_Builder.AppendFormat(str, arg0); }
        public void WriteFormat(string str, object arg0, object arg1)                   { m_Builder.AppendFormat(str, arg0, arg1); }
        public void WriteFormat(string str, object arg0, object arg1, object arg2)      { m_Builder.AppendFormat(str, arg0, arg1, arg2); }

        public void WriteLineFormat(string str, object arg0)                            { WriteFormat(str, arg0); WriteLine(); }
        public void WriteLineFormat(string str, object arg0, object arg1)               { WriteFormat(str, arg0, arg1); WriteLine(); }
        public void WriteLineFormat(string str, object arg0, object arg1, object arg2)  { WriteFormat(str, arg0, arg1, arg2); WriteLine(); }

        // Generic builder method
        public void Write<T>(T t)
        {
            m_Builder.Append(t);
        }

        // Optimize version to append substring and avoid useless allocation
        public void Write(String s,int start,int length)
        {
            m_Builder.Append(s, start, length);
        }

        public void WriteLine<T>(T t)
        {
            Write(t);
            WriteLine();
        }

        public void WriteLine()
        {
            m_Builder.AppendLine();
            WriteIndent();
        }

        public void EnterScope()
        {
            Indent();
            WriteLine('{');
        }

        public void ExitScope()
        {
            Deindent();
            m_Builder[m_Builder.Length - 1] = '}'; // Replace last '\t' by '}'
            WriteLine();
        }

        public void ExitScopeStruct()
        {
            Deindent();
            m_Builder[m_Builder.Length - 1] = '}'; // Replace last '\t' by '}'
            WriteLine(';');
        }

        public override string ToString()
        {
            return m_Builder.ToString();
        }

        private int WritePadding(int alignment,int offset,ref int index)
        {
            int padding = (alignment - (offset % alignment)) % alignment;
            if (padding != 0)
                WriteLineFormat("uint{0} _PADDING_{1};", padding == 1 ? "" : padding.ToString(), index++);
            return padding;
        }

        // Shader helper methods
        public void WriteAttributeBuffer(AttributeBuffer attributeBuffer,bool outputData = false)
        {
            if (outputData)
                WriteLine("struct OutputData");
            else
                WriteLineFormat("struct Attribute{0}",attributeBuffer.Index);

            EnterScope();

            int paddingIndex = 0;
            int offset = 0;

            for (int i = 0; i < attributeBuffer.Count; ++i)
            {
                int size = VFXValue.TypeToSize(attributeBuffer[i].m_Type);
                int padding = WritePadding(size == 3 ? 4 : size, offset, ref paddingIndex);

                WriteType(attributeBuffer[i].m_Type);
                WriteLineFormat(" {0};", attributeBuffer[i].m_Name);

                offset += size + padding;
            }

            int alignment = Math.Min(offset,4);
            WritePadding(alignment == 3 ? 4 : alignment, offset, ref paddingIndex);

            ExitScopeStruct();
            WriteLine();
        }

        public void WriteCBuffer(string cbufferName, HashSet<VFXExpression> uniforms, Dictionary<VFXExpression, string> uniformsToName)
        {
            if (uniforms.Count > 0)
            {
                Write("CBUFFER_START(");
                Write(cbufferName);
                WriteLine(")");

                foreach (var uniform in uniforms)
                {
                    Write('\t');
                    WriteType(uniform.ValueType);
                    Write(" ");
                    Write(uniformsToName[uniform]);
                    WriteLine(";");
                }

                WriteLine("CBUFFER_END");
                WriteLine();
            }
        }

        public void WriteSamplers(HashSet<VFXExpression> samplers, Dictionary<VFXExpression, string> samplersToName)
        {
            foreach (var sampler in samplers)
                WriteSampler(sampler.ValueType, samplersToName[sampler] + "Texture");
        }

        public void WriteSampler(VFXValueType samplerType,string name)
        {
            if (samplerType == VFXValueType.kTexture2D)
                Write("Texture2D ");
            else if (samplerType == VFXValueType.kTexture3D)
                Write("Texture3D ");
            else
                return;

            WriteLineFormat("{0};", name);
            WriteLineFormat("SamplerState sampler{0};", name);

            WriteLine();
        }

        public void WriteInitVFXSampler(VFXValueType samplerType,string name)
        {
            if (samplerType == VFXValueType.kTexture2D)
                WriteLineFormat("VFXSampler2D {0} = InitSampler({0}Texture,sampler{0}Texture);", name);
            else if (samplerType == VFXValueType.kTexture3D)
                WriteLineFormat("VFXSampler3D {0} = InitSampler({0}Texture,sampler{0}Texture);", name);
        }

        public void WriteType(VFXValueType type)
        {
            // tmp transform texture to sampler TODO This must be handled directly in C++ conversion array
            switch (type)
            {
                case VFXValueType.kTexture2D:       Write("VFXSampler2D"); break;
                case VFXValueType.kTexture3D:       Write("VFXSampler3D"); break;
                case VFXValueType.kCurve:           Write("float4"); break;
                case VFXValueType.kColorGradient:   Write("float"); break;
                default:                            Write(VFXValue.TypeToName(type)); break;
            }
        }

        public void WriteFunction(VFXBlockModel block, HashSet<string> functions, ShaderMetaData data)
        {
            VFXGeneratedTextureData texData = data.generatedTextureData;
            if (!functions.Contains(block.Desc.FunctionName)) // if not already defined
            {
                functions.Add(block.Desc.FunctionName);

                string source = block.Desc.Source;

                bool hasCurve = false;
                bool hasGradient = false;
                foreach (var property in block.Desc.Properties)
                    if (property.m_Type.ValueType == VFXValueType.kColorGradient)
                        hasGradient = true;
                    else if (property.m_Type.ValueType == VFXValueType.kCurve)
                        hasCurve = true;

                // function signature
                Write("void ");
                Write(block.Desc.FunctionName);
                Write("(");

                WriteGenericFunctionInterface(
                    block,
                    data,
                    (arg) =>
                    {
                        if (arg.m_Writable)
                            Write("inout ");
                        WriteType(arg.m_Type);
                        Write(" ");
                        Write(arg.m_Name);
                    },
                    (arg, inv, exp) =>
                    {
                        if (arg != null)
                        {
                            WriteType(arg.m_Value.ValueType);
                            Write(inv ? " Inv" : " ");
                            Write(arg.m_Name);
                        }
                        else
                        {
                            var builtInExp = exp as VFXExpressionBuiltInValue;
                            Write(builtInExp.Declaration);
                        }

                    },
                    () => Write("inout bool kill")
                );
                WriteLine(")");

                // function body
                EnterScope();

                if (hasGradient || hasCurve)
                    WriteSourceWithSamplesResolved(source,block,texData);
                else
                    Write(source);
                WriteLine();

                ExitScope();
                WriteLine();
            }
        }

        public void WriteGenericFunctionInterface(VFXBlockModel block, ShaderMetaData data, Action<VFXAttribute> fnWriteAttribute, Action<VFXNamedValue, bool, VFXExpression> fnWriteExpression, Action fnKill)
        {
            char separator = ' ';
            foreach (var arg in block.Desc.Attributes)
            {
                Write(separator);
                separator = ',';
                fnWriteAttribute(arg);
            }

            List<VFXNamedValue> namedValues = new List<VFXNamedValue>();
            for (int i = 0; i < block.GetNbSlots(); ++i)
            {
                VFXPropertySlot slot = block.GetSlot(i);

                namedValues.Clear();
                slot.CollectNamedValues(namedValues, data.system.GetSpaceRef());
                foreach (var arg in namedValues)
                    if (arg.m_Value.IsValue())
                    {
                        Write(separator);
                        separator = ',';
                        fnWriteExpression(arg, false, arg.m_Value);
                    }

                // Write extra parameters
                foreach (var arg in namedValues)
                    if (arg.m_Value.IsValue() && arg.m_Value.ValueType == VFXValueType.kTransform && block.Desc.IsSet(VFXBlockDesc.Flag.kNeedsInverseTransform))
                    {
                        VFXExpression extraValue = data.extraUniforms[arg.m_Value];
                        if (extraValue.IsValue())
                        {
                            Write(separator);
                            separator = ',';
                            fnWriteExpression(arg, true, extraValue);
                        }
                    }
            }

            if (block.Desc.IsSet(VFXBlockDesc.Flag.kNeedsDeltaTime))
            {
                Write(separator);
                separator = ',';
                fnWriteExpression(null, false, CommonGlobalExpression.DeltaTime);
            }

            if (block.Desc.IsSet(VFXBlockDesc.Flag.kNeedsTotalTime))
            {
                Write(separator);
                separator = ',';
                fnWriteExpression(null, false, CommonGlobalExpression.TotalTime);
            }

            if (block.Desc.IsSet(VFXBlockDesc.Flag.kHasRand))
            {
                Write(separator);
                separator = ',';
                fnWriteAttribute(CommonAttrib.Seed);
            }

            if (block.Desc.IsSet(VFXBlockDesc.Flag.kHasKill))
            {
                Write(separator);
                separator = ',';
                fnKill();
            }
        }

        // TODO source shouldnt be a parameter but taken from block
        private void WriteSourceWithSamplesResolved(string source,VFXBlockModel block, VFXGeneratedTextureData texData)
        {
            int lastIndex = 0;
            int indexSample = 0;
            while ((indexSample = source.IndexOf("SAMPLE", lastIndex)) != -1)
            {
                Write(source, lastIndex, indexSample - lastIndex);
                Write("sampleSignal");
                lastIndex = indexSample + 6; // size of "SAMPLE"
            }

            Write(source, lastIndex, source.Length - lastIndex); // Write the rest of the source
        }

        public void WriteFunctionCall(
            VFXBlockModel block,
            HashSet<string> functions,
            ShaderMetaData data,
            bool output)
        {
            Dictionary<VFXExpression, string> paramToName = output ? data.outputParamToName : data.paramToName;

            UnityEngine.Profiling.Profiler.BeginSample("WriteFunctionCall");

            Write(block.Desc.FunctionName);
            Write("(");

            WriteGenericFunctionInterface(block,
                                            data,
                                            (arg) => WriteAttrib(arg, data, output),
                                            (arg, inv, exp) => Write(paramToName[exp]),
                                            () => Write("kill"));
            WriteLine(");");
            UnityEngine.Profiling.Profiler.EndSample();
        }

        public void WriteAddPhaseShift(ShaderMetaData data)
        {
            WritePhaseShift('+', data);
        }

        public void WriteRemovePhaseShift(ShaderMetaData data)
        {
            WritePhaseShift('-', data);
        }

        private void WritePhaseShift(char op, ShaderMetaData data)
        {
            WriteAttrib(CommonAttrib.Position, data);
            Write(" ");
            Write(op);
            Write("= (");
            WriteAttrib(CommonAttrib.Phase, data);
            Write(string.Format(" * {0}) * ", data.outputParamToName[CommonGlobalExpression.DeltaTime]));
            WriteAttrib(CommonAttrib.Velocity, data);
            WriteLine(";");
        }

        public void WriteAttrib(VFXAttribute attrib, ShaderMetaData data, bool output = false)
        {
            Write(GenerateAttrib(attrib, data, output));
        }

        public string GenerateAttrib(VFXAttribute attrib, ShaderMetaData data, bool output = false)
        {
            AttributeBuffer buffer;
            if (data.attribToBuffer.TryGetValue(attrib,out buffer))
            {
                if (output && data.outputBuffer != null)
                    return string.Format("outputData.{0}", attrib.m_Name);
                else
                    return string.Format("attrib{0}.{1}", buffer.Index, attrib.m_Name);
            }
            else // local attribute (dont even check if it is in the localAttrib dictionary but we should for consistency)
                return string.Format("local_{0}", attrib.m_Name);
        }

        public void WriteLocalAttribDeclaration(ShaderMetaData data,VFXContextDesc.Type context)
        {
            bool hasWritten = false;
            foreach (var attrib in data.localAttribs)
                if (VFXAttribute.Used(attrib.Value,context))
                {
                    WriteType(attrib.Key.m_Type);
                    WriteFormat(" local_{0} = (", attrib.Key.m_Name);
                    WriteType(attrib.Key.m_Type);
                    WriteLine(")0;");
                    hasWritten = true;
                }
            if (hasWritten)
                WriteLine();
        }

        public void WriteKernelHeader(string name)
        {
            WriteLine("[numthreads(NB_THREADS_PER_GROUP,1,1)]");
            Write("void ");
            Write(name);
            WriteLine("(uint3 id : SV_DispatchThreadID,uint3 groupId : SV_GroupThreadID)");
            EnterScope();
        }

        // Private stuff
        private void Indent()
        {
            ++m_Indent;
        }

        private void Deindent()
        {
            if (m_Indent == 0)
                throw new InvalidOperationException("Cannot de-indent as current indentation is 0");

            --m_Indent;
        }

        private void WriteIndent()
        {
            for (int i = 0; i < m_Indent; ++i)
                m_Builder.Append('\t');
        }

        private StringBuilder m_Builder = new StringBuilder();
        private int m_Indent = 0;
    }
}
