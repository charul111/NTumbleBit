﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NTumbleBit.PuzzlePromise;
using NTumbleBit.PuzzleSolver;
using NTumbleBit.ClassicTumbler;
using NTumbleBit.TumblerServer.Models;
using System.Net;
using NBitcoin;
using NTumbleBit.TumblerServer.Services;
using NBitcoin.Crypto;

// For more information on enabling Web API for empty projects, visit http://go.microsoft.com/fwlink/?LinkID=397860

namespace NTumbleBit.TumblerServer.Controllers
{
	public class MainController : Controller
	{
		public MainController(TumblerConfiguration configuration, ClassicTumblerParameters parameters, ServerServices services)
		{
			if(configuration == null)
				throw new ArgumentNullException("configuration");
			if(parameters == null)
				throw new ArgumentNullException("parameters");
			if(services == null)
				throw new ArgumentNullException("services");

			_Tumbler = configuration;
			_Repository = configuration.Repository;
			_Parameters = parameters;
			_Services = services;
		}


		private readonly ServerServices _Services;
		public ServerServices Services
		{
			get
			{
				return _Services;
			}
		}

		private readonly ClassicTumblerRepository _Repository;
		public ClassicTumblerRepository Repository
		{
			get
			{
				return _Repository;
			}
		}

		private readonly TumblerConfiguration _Tumbler;
		public TumblerConfiguration Tumbler
		{
			get
			{
				return _Tumbler;
			}
		}


		private readonly ClassicTumblerParameters _Parameters;
		public ClassicTumblerParameters Parameters
		{
			get
			{
				return _Parameters;
			}
		}

		[HttpGet("api/v1/tumblers/0/parameters")]
		public ClassicTumblerParameters GetSolverParameters()
		{
			return Parameters;
		}

		[HttpGet("api/v1/tumblers/0/vouchers")]
		public AskVoucherResponse AskVoucherParameters()
		{
			var height = Services.BlockExplorerService.GetCurrentHeight();
			var cycleParameters = Parameters.CycleGenerator.GetRegistratingCycle(height);
			var bobSession = new TumblerBobServerSession(Parameters, Tumbler.TumblerKey, Tumbler.VoucherKey, cycleParameters.Start);
			PuzzleSolution solution = null;
			var voucher = bobSession.GenerateUnsignedVoucher(ref solution);
			Repository.Save(GetKey(solution), bobSession);
			return new AskVoucherResponse()
			{
				Cycle = cycleParameters.Start,
				UnsignedVoucher = voucher
			};
		}


		[HttpPost("api/v1/tumblers/0/clientchannels")]
		public IActionResult RequestTumblerEscrowKey([FromBody]ClientEscrowInformation request)
		{
			var height = Services.BlockExplorerService.GetCurrentHeight();
			var aliceSession = new TumblerAliceServerSession(Parameters, Tumbler.TumblerKey, Tumbler.VoucherKey);
			var pubKey = aliceSession.ReceiveAliceEscrowInformation(request);
			if(!aliceSession.GetCycle().IsInPhase(CyclePhase.ClientChannelEstablishment, height))
				return BadRequest("incorrect-phase");
			Repository.Save(aliceSession.GetChannelId(), aliceSession);
			Services.BlockExplorerService.Track(aliceSession.BuildEscrowTxOut().ScriptPubKey);
			return Json(pubKey);
		}		

		[HttpPost("api/v1/tumblers/0/clientchannels/confirm")]
		public IActionResult ClientChannelConfirmed([FromBody]uint256 txId)
		{
			var transaction = Services.BlockExplorerService.GetTransaction(txId);
			if(transaction == null || 
				(transaction.Confirmations < Parameters.CycleGenerator.FirstCycle.SafetyPeriodDuration))
				return BadRequest("not-enough-confirmation");
			if(transaction.Transaction.Outputs.Count > 2)
				return BadRequest("invalid-transaction");

			var sessions = transaction
				.Transaction
				.Outputs
				.Select(o => Repository.GetAliceSession(o.ScriptPubKey.ToHex()))
				.Where(o => o != null)
				.ToList();
			if(sessions.Count != 1)
				return BadRequest("invalid-transaction");

			var height = Services.BlockExplorerService.GetCurrentHeight();
			if(!sessions[0].GetCycle().IsInPhase(CyclePhase.ClientChannelEstablishment, height))
				return BadRequest("incorrect-phase");

			var channelId = sessions[0].GetChannelId();
			PuzzleSolution voucher;
			var solverServerSession = sessions[0].ConfirmAliceEscrow(transaction.Transaction, out voucher);
			Repository.Save(channelId, sessions[0]);
			Repository.Save(solverServerSession);
			return Json(voucher);
		}

		[HttpPost("api/v1/tumblers/0/channels")]
		public IActionResult OpenChannel([FromBody] BobEscrowInformation request)
		{
			var height = Services.BlockExplorerService.GetCurrentHeight();
			var session = Repository.GetBobSession(GetKey(request.SignedVoucher));
			if(session == null)
				return NotFound("channel-not-found");
			if(!session.GetCycle().IsInPhase(CyclePhase.TumblerChannelEstablishment, height))
				return BadRequest("incorrect-phase");
			var fee = Services.FeeService.GetFeeRate();
			try
			{
				session.ReceiveBobEscrowInformation(request);
				var txOut = session.BuildEscrowTxOut();
				var tx = Services.WalletService.FundTransaction(txOut, fee);
				if(tx == null)
					return BadRequest("tumbler-insufficient-funds");
				Services.BlockExplorerService.Track(txOut.ScriptPubKey);
				Services.BroadcastService.Broadcast(tx);
				var promiseServerSession = session.SetSignedTransaction(tx);
				Repository.Save(GetKey(request.SignedVoucher), session);
				Repository.Save(promiseServerSession);
				return this.Json(promiseServerSession.EscrowedCoin);
			}
			catch(PuzzleException)
			{
				return BadRequest("incorrect-voucher");
			}
		}


		[HttpPost("api/v1/tumblers/0/channels/{channelId}/signhashes")]
		public IActionResult SignHashes(string channelId, [FromBody]SignaturesRequest sigReq)
		{
			var session = GetPromiseServerSession(channelId, CyclePhase.TumblerChannelEstablishment);			
			var hashes = session.SignHashes(sigReq);
			Repository.Save(session);
			return Json(hashes);
		}

		[HttpPost("api/v1/tumblers/0/channels/{channelId}/checkrevelation")]
		public IActionResult CheckRevelation(string channelId, [FromBody]PuzzlePromise.ClientRevelation revelation)
		{
			var session = GetPromiseServerSession(channelId, CyclePhase.TumblerChannelEstablishment);
			var proof = session.CheckRevelation(revelation);
			Repository.Save(session);
			return Json(proof);
		}

		private PromiseServerSession GetPromiseServerSession(string channelId, CyclePhase expectedPhase)
		{
			var height = Services.BlockExplorerService.GetCurrentHeight();
			var session = Repository.GetPromiseServerSession(channelId);
			if(session == null)
				throw NotFound("channel-not-found").AsException();
			var lockTime = EscrowScriptBuilder.ExtractEscrowScriptPubKeyParameters(session.EscrowedCoin.Redeem).LockTime;
			var firstCycle = Parameters.CycleGenerator.GetCycle(Parameters.CycleGenerator.FirstCycle.Start);
			var lockOffset = (uint)firstCycle.GetTumblerLockTime() - firstCycle.Start;
			var start = checked((uint)lockTime - lockOffset);
			var cycle = Parameters.CycleGenerator.GetCycle(checked((int)start));
			if(!cycle.IsInPhase(expectedPhase, height))
				throw BadRequest("invalid-phase").AsException();
			return session;
		}

		private SolverServerSession GetSolverServerSession(string channelId, CyclePhase expectedPhase)
		{
			var height = Services.BlockExplorerService.GetCurrentHeight();
			var session = Repository.GetSolverServerSession(channelId);
			if(session == null)
				throw NotFound("channel-not-found").AsException();
			var lockTime = EscrowScriptBuilder.ExtractEscrowScriptPubKeyParameters(session.EscrowedCoin.Redeem).LockTime;
			var firstCycle = Parameters.CycleGenerator.GetCycle(Parameters.CycleGenerator.FirstCycle.Start);
			var lockOffset = (uint)firstCycle.GetClientLockTime() - firstCycle.Start;
			var start = checked((uint)lockTime - lockOffset);
			var cycle = Parameters.CycleGenerator.GetCycle(checked((int)start));
			if(!cycle.IsInPhase(expectedPhase, height))
				throw BadRequest("invalid-phase").AsException();
			return session;
		}

		[HttpPost("api/v1/tumblers/0/clientchannels/{channelId}/solvepuzzles")]
		public IActionResult SolvePuzzles(string channelId, [FromBody]PuzzleValue[] puzzles)
		{
			var session = GetSolverServerSession(channelId, CyclePhase.PaymentPhase);
			var commitments = session.SolvePuzzles(puzzles);
			Repository.Save(session);
			return Json(commitments);
		}

		[HttpPost("api/v1/tumblers/0/clientschannels/{channelId}/checkrevelation")]
		public IActionResult CheckRevelation(string channelId, [FromBody]PuzzleSolver.ClientRevelation revelation)
		{
			var session = GetSolverServerSession(channelId, CyclePhase.PaymentPhase);
			var solutions = session.CheckRevelation(revelation);
			Repository.Save(session);
			return Json(solutions);
		}

		[HttpPost("api/v1/tumblers/0/clientschannels/{channelId}/checkblindfactors")]
		public IActionResult CheckBlindFactors(string channelId, [FromBody]BlindFactor[] blindFactors)
		{
			var session = GetSolverServerSession(channelId, CyclePhase.PaymentPhase);
			session.CheckBlindedFactors(blindFactors);
			Repository.Save(session);
			return Json(true);
		}			

		private string GetKey(PuzzleSolution signedVoucher)
		{
			return Hashes.Hash160(signedVoucher.ToBytes()).ToString();
		}
	}
}