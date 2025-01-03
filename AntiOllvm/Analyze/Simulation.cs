﻿using AntiOllvm.Analyze.Impl;
using AntiOllvm.entity;
using AntiOllvm.Extension;
using AntiOllvm.Helper;
using AntiOllvm.Logging;

namespace AntiOllvm.Analyze;

public class Simulation
{
    private readonly List<Block> _blocks;
    private IAnalyze _analyzer;
    private readonly RegisterContext _regContext;
    private Instruction _lastCompareIns;
    private Block _initBlock;
    public RegisterContext RegContext => _regContext;
    public IAnalyze Analyzer => _analyzer;
    private string _outJsonPath;
    private IDACFG _idacfg;


    private List<Block> _childDispatcherBlocks = new List<Block>();

    private List<Block> _realBlocks = new List<Block>();


    public Simulation(string json, string outJsonPath)
    {
        _outJsonPath = outJsonPath;
        _idacfg = JsonHelper.Format<IDACFG>(json);
        _blocks = _idacfg.cfg;
        _regContext = new RegisterContext();
        Logger.InfoNewline("Simulation initialized with " + _blocks.Count + " blocks");
    }

    public void SetAnalyze(IAnalyze iAnalyze)
    {
        _analyzer = iAnalyze;
    }

    public void Run()
    {
        _analyzer.Init(_blocks, this);
        //get the main entry block
        foreach (var block in _blocks)
        {
            if (_analyzer.IsInitBlock(block, this))
            {
                _initBlock = block;
                Logger.RedNewline("Init block found  " + block.start_address);
                break;
            }
        }

        if (_initBlock == null)
        {
            throw new Exception("Init block not found you should implement IsInitBlock method and find it ");
        }

        ReBuildCFGBlocks();
    }

    private void ReBuildCFGBlocks()
    {
        var block = FindRealBlock(_initBlock);
        if (block == null)
        {
            Logger.RedNewline("Init block not found in real blocks");
            return;
        }

        Logger.RedNewline("=========================================================\n" +
                          "=========================================================");
        Logger.InfoNewline("Start Fix ReadBlock Count is  " + _realBlocks.Count);
        //Order by Address 
        _realBlocks = _realBlocks.OrderBy(x => x.start_address).ToList();


        foreach (var realBlock in _realBlocks)
        {
            if (realBlock.isFix)
            {
                continue;
            }

            realBlock.FixMachineCode(this);
        }

        List<Instruction> fixInstructions = new List<Instruction>();
        FixDispatcher(fixInstructions);
        Logger.InfoNewline("Child Dispatcher Count " + _childDispatcherBlocks.Count
                                                     + " RealBlock Fix Start " + fixInstructions.Count);

        foreach (var realBlock in _realBlocks)
        {
            foreach (var instruction in realBlock.instructions)
            {
                if (!string.IsNullOrEmpty(instruction.fixmachine_code))
                {
                    if (!fixInstructions.Contains(instruction))
                    {
                        fixInstructions.Add(instruction);
                    }
                }

                if (!string.IsNullOrEmpty(instruction.fixmachine_byteCode))
                {
                    if (!fixInstructions.Contains(instruction))
                    {
                        fixInstructions.Add(instruction);
                    }
                }
            }
        }

        File.WriteAllText(_outJsonPath, JsonHelper.ToString(fixInstructions));

        OutLogger.InfoNewline("All Instruction is Fix Done Count is " + fixInstructions.Count);
        OutLogger.InfoNewline("FixJson OutPath is " + _outJsonPath);
    }

    private void FixDispatcher(List<Instruction> fixInstructions)
    {
        foreach (var block in _childDispatcherBlocks)
        {
            foreach (var instruction in block.instructions)
            {
                if (string.IsNullOrEmpty(instruction.fixmachine_code))
                {
                    if (instruction.InstructionSize == 8)
                    {
                        instruction.SetFixMachineCode("NOP");
                        var nop = Instruction.CreateNOP($"0x{instruction.GetAddress() + 4:X}");
                        fixInstructions.Add(instruction);
                        fixInstructions.Add(nop);
                    }
                    else
                    {
                        instruction.SetFixMachineCode("NOP");
                        fixInstructions.Add(instruction);
                    }
                }
                else
                {
                    fixInstructions.Add(instruction);
                }
            }
        }
    }

    private void AssignRegisterByInstruction(Instruction instruction)
    {
        switch (instruction.mnemonic)
        {
            case "LDR":
            {
                var dest = instruction.Operands()[0];
                var src = instruction.Operands()[1];
                if (src is { kind: Arm64OperandKind.Memory, memoryOperand.registerName: "SP" })
                {
                    var sp = _regContext.GetRegister(src.memoryOperand.registerName).value as SPRegister;
                    var imm = sp.Get(src.memoryOperand.addend);
                    var reg = _regContext.GetRegister(dest.registerName);
                    reg.SetLongValue(imm);
                    Logger.RedNewline($"AssignRegisterByInstruction LDR {dest.registerName} = {imm} ({imm:X})");
                }

                break;
            }
            case "MOV":
            {
                //Assign register
                var left = instruction.Operands()[0];
                var right = instruction.Operands()[1];
                if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Immediate)
                {
                    //Assign immediate value to register
                    var register = _regContext.GetRegister(left.registerName);
                    var imm = right.immediateValue;
                    register.SetLongValue(imm);
                    Logger.RedNewline($"AssignRegisterByInstruction MOV {left.registerName} = {imm} ({imm:X})");
                }

                if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Register)
                {
                    var leftReg = _regContext.GetRegister(left.registerName);
                    var rightReg = _regContext.GetRegister(right.registerName);
                    leftReg.SetLongValue(rightReg.GetLongValue());
                    Logger.RedNewline(
                        $"AssignRegisterByInstruction MOV {left.registerName} = {right.registerName} ({rightReg.GetLongValue():X})");
                }
            }
                break;
            case "MOVK":
            {
                var dest = instruction.Operands()[0];
                var imm = instruction.Operands()[1].immediateValue;
                var shift = instruction.Operands()[2].shiftType;
                var reg = _regContext.GetRegister(dest.registerName);
                var v = MathHelper.CalculateMOVK(reg.GetLongValue(), imm, shift, instruction.Operands()[2].shiftValue);
                reg.SetLongValue(v);
                Logger.InfoNewline($"AssignRegisterByInstruction MOVK {dest.registerName} = {imm} ({imm:X})");
                break;
            }
        }
    }

    private Block RunDispatcherBlock(Block block)
    {
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.LDR:
                {
                    AssignRegisterByInstruction(instruction);
                    break;
                }
                case OpCode.MOVK:
                {
                    AssignRegisterByInstruction(instruction);
                    Logger.InfoNewline("MOVK " + instruction.Operands()[0].registerName + " = " +
                                       instruction.Operands()[1].immediateValue + " in DispatchBlock ============");
                    break;
                }
                case OpCode.MOV:
                {
                    //Runnning MOV instruction in Dispatch we need sync this
                    AssignRegisterByInstruction(instruction);

                    break;
                }
                case OpCode.B:
                {
                    var addr = instruction.GetRelativeAddress();
                    var nextBlock = FindBlockByAddress(addr);
                    if (block.IsChildBlock(nextBlock))
                    {
                        return nextBlock;
                    }

                    throw new Exception(" B " + instruction + " is not in " + block);
                }
                case OpCode.CMP:
                {
                    var left = instruction.Operands()[0];
                    var right = instruction.Operands()[1];
                    //Dispatcher value not zero 
                    var leftImm = _regContext.GetRegister(left.registerName).GetLongValue();
                    var rightImm = _regContext.GetRegister(right.registerName).GetLongValue();
                    if (leftImm == 0 || rightImm == 0)
                    {
                        throw new Exception(" is error CMP value is zero");
                    }

                    _regContext.Compare(left.registerName, right.registerName);
                    _lastCompareIns = instruction;
                    break;
                }
                case OpCode.B_NE:
                case OpCode.B_EQ:
                case OpCode.B_GT:
                case OpCode.B_LE:
                {
                    var needJump = ConditionJumpHelper.Condition(instruction.Opcode(), _lastCompareIns, _regContext);
                    Block jumpBlock;
                    //next block is current Address +4 ;

                    jumpBlock = !needJump
                        ? FindBlockByAddress(instruction.GetAddress() + 4)
                        : FindBlockByAddress(instruction.GetRelativeAddress());
                    Logger.VerboseNewline("\n block  " + block + "\n is Jump ? " + needJump + " next block is " +
                                          jumpBlock.start_address);

                    if (block.IsChildBlock(jumpBlock))
                    {
                        return jumpBlock;
                    }

                    throw new Exception(
                        $" Analyze Error :  {jumpBlock.start_address} is not in {block.start_address} Child ");
                    // break;
                }
                default:
                {
                    throw new Exception(" not support opcode " + instruction.Opcode());
                }
            }
        }

        if (block.instructions.Count == 1)
        {
            //Fix only MOV instruction Block
            var nextBlock = block.GetLinkedBlocks(this)[0];
            return nextBlock;
        }

        return null;
    }

    private Block FindRealBlock(Block block)
    {
        if (_analyzer.IsDispatcherBlock(block, this))
        {
            Logger.InfoNewline("is Dispatcher block " + block.start_address);
            var next = RunDispatcherBlock(block);
            if (!_childDispatcherBlocks.Contains(block))
            {
                _childDispatcherBlocks.Add(block);
            }

            return FindRealBlock(next);
        }

        if (_analyzer.IsRealBlock(block, this))
        {
            block.RealChilds = GetAllChildBlocks(block);
            if (!_realBlocks.Contains(block))
            {
                _realBlocks.Add(block);
            }

            return block;
        }

        throw new Exception("is unknown block \n" + block);
    }

    private void SyncLogicInstruction(Instruction instruction, Block block)
    {
        switch (instruction.Opcode())
        {
            case OpCode.MOV:
            {
                //Assign register
                var left = instruction.Operands()[0];
                var right = instruction.Operands()[1];
                if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Immediate)
                {
                    //Assign immediate value to register
                    var register = _regContext.GetRegister(left.registerName);
                    var imm = right.immediateValue;
                    register.SetLongValue(imm);
                    Logger.RedNewline($"Update  MOV {left.registerName} = {imm} ({imm:X})");
                }

                break;
            }
            case OpCode.MOVK:
            {
                var dest = instruction.Operands()[0];
                var imm = instruction.Operands()[1].immediateValue;
                var shift = instruction.Operands()[2].shiftType;
                var reg = _regContext.GetRegister(dest.registerName);
                var v = MathHelper.CalculateMOVK(reg.GetLongValue(), imm, shift, instruction.Operands()[2].shiftValue);
                reg.SetLongValue(v);
                Logger.InfoNewline($"Update MOVK {dest.registerName} = {imm} ({imm:X})");
                break;
            }
        }
    }

    private void SyncLogicBlock(Block block)
    {
        foreach (var instruction in block.instructions)
        {
            SyncLogicInstruction(instruction, block);
        }
    }

    private Instruction IsRealBlockHasCSELDispatcher(Block block)
    {
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.CSEL:
                {
                    if (_analyzer.IsCSELOperandDispatchRegister(instruction, this))
                    {
                        return instruction;
                    }

                    break;
                }
            }
        }

        return null;
    }

    private List<Block> GetAllChildBlocks(Block block)
    {
        if (block.isFind)
        {
            Logger.WarnNewline("block is Finding  " + block.start_address);
            return block.RealChilds;
        }

        block.isFind = true;
        var list = new List<Block>();
        var isRealBlockDispatcherNext =
            _analyzer.IsRealBlockWithDispatchNextBlock(block, this);

        if (isRealBlockDispatcherNext)
        {
            SyncLogicBlock(block);
        }

        var cselInstruction = IsRealBlockHasCSELDispatcher(block);
        if (cselInstruction != null)
        {
            //Mark this when we fixMachineCode 
            block.CFF_CSEL = cselInstruction;

            Logger.WarnNewline("block has CSEL Dispatcher " + block);
            _regContext.SnapshotRegisters(block.start_address);
            var needOperandRegister = cselInstruction.Operands()[0].registerName;
            var operandLeft = cselInstruction.Operands()[1].registerName;
            var left = _regContext.GetRegister(operandLeft);
            Logger.InfoNewline(" Write left Imm " + left.value);
            _regContext.SetRegister(needOperandRegister, left.value);
            var nextBlock = block.GetLinkedBlocks(this)[0];
            //Before we find real block we need supply some register value
            SupplyBlockIfNeed(block, cselInstruction);
            var leftBlock = FindRealBlock(nextBlock);
            Logger.WarnNewline("Block " + block.start_address + " Left  is Link To " + leftBlock.start_address);
            list.Add(leftBlock);
            _regContext.RestoreRegisters(block.start_address);
            var operandRight = cselInstruction.Operands()[2].registerName;
            var right = _regContext.GetRegister(operandRight);
            _regContext.SetRegister(needOperandRegister, right.value);
            SupplyBlockIfNeed(block, cselInstruction);
            var rightBlock = FindRealBlock(nextBlock);
            Logger.WarnNewline("Block " + block.start_address + " Right  is Link To " + rightBlock.start_address);
            list.Add(rightBlock);
            return list;
        }

        if (isRealBlockDispatcherNext)
        {
            var linkedBlocks = block.GetLinkedBlocks(this);
            if (linkedBlocks.Count != 1)
            {  
                Logger.WarnNewline("Real Block will be  Dispatcher Next with two bransh \n" + block);
                var next = FindRealBlock(linkedBlocks[0]);
                list.Add(next);
                next = FindRealBlock(linkedBlocks[1]);
                list.Add(next);
                return list;
            }
            Logger.WarnNewline("Real Block Dispatcher Next \n" + block);
            var nextBlock = FindRealBlock(linkedBlocks[0]);
            list.Add(nextBlock);
            return list;
        }

        //But we need Check next is dispatcher block
        var links = block.GetLinkedBlocks(this);
        if (links.Count == 0)
        {
            Logger.WarnNewline("Real Block is end block " + block);
            return list;
        }

        if (links.Count == 2)
        {   
            Logger.WarnNewline(" Real Block  and not dispatcher next with two bransh "
            +"  left is " + links[0].start_address + " right is " + links[1].start_address +"\n"+block);
            
            var next = FindRealBlock(links[0]);
            list.Add(next);
            next = FindRealBlock(links[1]);
            list.Add(next);
            return list;
        }

        Logger.WarnNewline("Real Block  and not dispatcher next with one bransh " + block);
        list.Add(FindRealBlock(links[0]));
        return list;
    }

    /**
     * When csel is dispatcher, but it's some register value need supply
     */
    private void SupplyBlockIfNeed(Block block, Instruction csel)
    {
        // CSEL            W8, W12, W19, EQ
        // MOVK            W9, #0x94FC,LSL#16  it's sync in  SyncLogicBlock(block);
        // STR             W8, [SP,#0x330+var_2AC]
        var index = block.instructions.IndexOf(csel);
        //Got next instruction
        for (int i = 0; i < block.instructions.Count; i++)
        {
            if (i <= index)
            {
                continue;
            }

            var instruction = block.instructions[i];
            if (instruction.Opcode() == OpCode.STR)
            {
                var dest = instruction.Operands()[1];
                var src = instruction.Operands()[0];
                if (dest.kind == Arm64OperandKind.Memory)
                {
                    var cselFirstOperand = csel.Operands()[0];
                    if (cselFirstOperand.registerName == src.registerName)
                    {
                        //dynamics supply register value
                        var reg = _regContext.GetRegister(src.registerName);
                        var sp = _regContext.GetRegister(dest.memoryOperand.registerName).value as SPRegister;
                        Logger.RedNewline(" Supply Register Value " + src.registerName + " = " +
                                          reg.GetLongValue().ToString("X")
                                          + " offset is  " + dest.memoryOperand.addend);
                        sp?.Put(dest.memoryOperand.addend, reg.GetLongValue());
                    }
                }
            }
        }
    }

    public Block FindBlockByAddress(long address)
    {
        foreach (var block in _blocks)
        {
            if (block.GetStartAddress() == address)
            {
                return block;
            }
        }

        return null;
    }

    public bool IsDispatcherBlock(Block link)
    {
        if (_childDispatcherBlocks.Contains(link))
        {
            return true;
        }

        return _analyzer.IsDispatcherBlock(link, this);
    }
}