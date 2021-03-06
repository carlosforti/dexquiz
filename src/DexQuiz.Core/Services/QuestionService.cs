﻿using DexQuiz.Core.Entities;
using DexQuiz.Core.Enums;
using DexQuiz.Core.Interfaces.Repositories;
using DexQuiz.Core.Interfaces.Services;
using DexQuiz.Core.Interfaces.UoW;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DexQuiz.Core.Services
{
    public class QuestionService : IQuestionService
    {
        private const short AmountOfTrackQuestionsForUser = 12;
        private const short AmountOfTrackQuestionsPerLevel = 4;

        private readonly IUnitOfWork _unitOfWork;
        private readonly IQuestionRepository _questionRepository;
        private readonly ITrackRepository _trackRepository;
        private readonly IAnswerRepository _answerRepository;
        private readonly IAnsweredQuestionRepository _answeredQuestionRepository;
        private readonly IAvailableQuestionRepository _availableQuestionRepository;

        public QuestionService(IUnitOfWork unitOfWork,
            IQuestionRepository questionRepository,
            ITrackRepository trackRepository,
            IAnswerRepository answerRepository,
            IAnsweredQuestionRepository answeredQuestionRepository,
            IAvailableQuestionRepository availableQuestionRepository)
        {
            _unitOfWork = unitOfWork;
            _questionRepository = questionRepository;
            _trackRepository = trackRepository;
            _answerRepository = answerRepository;
            _answeredQuestionRepository = answeredQuestionRepository;
            _availableQuestionRepository = availableQuestionRepository;
        }

        public async Task<bool> AddQuestionAsync(Question question)
        {
            if (!await IsTrackAvailableAsync(question.TrackId))
            {
                throw new Exception($"This track id {question.TrackId} is not available");
            }
            else if (question.Answers.Count <= 1)
            {
                throw new Exception("A questão deve ter mais de uma alternativa possível.");
            }
            else if (question.Answers.Count(q => q.IsAnswerCorrect) != 1)
            {
                throw new Exception("A questão deve ter apenas uma resposta correta.");
            }

            await _questionRepository.AddAsync(question);
            await _unitOfWork.CommitAsync();
            return true;
        }

        public async Task DeleteQuestionAsync(int questionId)
        {
            if (await DoesQuestionHaveUserAnswersAsync(questionId))
            {
                throw new Exception("A questão não pode ser excluída, pois usuários já responderam ela.");
            }

            foreach (var existingAnswer in await _answerRepository.FindAsync(a => a.QuestionId == questionId))
            {
                _answerRepository.Remove(existingAnswer);
            }
            var question = await FindQuestionByIdAsync(questionId);
            _questionRepository.Remove(question);
            await _unitOfWork.CommitAsync();
        }

        public async Task<bool> DoesAnswerBelongToQuestionAsync(int answerId, int questionId)
        {
            return (await _answerRepository.FindAsync(x => x.Id == answerId && x.QuestionId == questionId)) != null;
        }

        public async Task<Question> FindQuestionByIdAsync(int id) =>
            await _questionRepository.FindAsync(id);

        public async Task<IEnumerable<Question>> GetTrackQuestionsAsync(int trackId) =>
            await _questionRepository.FindAsync(q => q.TrackId == trackId);

        public async Task<bool> HasQuestionBeenAnsweredByUserAsync(int userId, int questionId) =>
            (await _answeredQuestionRepository
                .FindAsync(aq => aq.UserId == userId && aq.QuestionId == questionId)).Any();

        public async Task<bool> HasUserFinishedTrackAsync(int userId, int trackId) =>
            await _trackRepository.IsTrackDoneByUserAsync(userId, trackId);

        public async Task<bool> HasUserFinishedTrackAsync(AnsweredQuestion answeredQuestion)
        {
            var trackId = (await _questionRepository.FindAsync(answeredQuestion.QuestionId))?.TrackId ?? 0;
            return await HasUserFinishedTrackAsync(answeredQuestion.UserId, trackId);
        }

        public async Task InitializeQuestionsForUserIfNotCreatedAsync(int userId, int trackId)
        {
            if (!await HaveUserQuestionsBeenCreatedAsync(userId, trackId))
            {
                var random = new Random();
                IEnumerable<AvailableQuestion> selectedQuestionsForUser = CreateTrackQuestionsForUser(userId, trackId, random);

                foreach (var selectedQuestion in selectedQuestionsForUser)
                {
                    await _availableQuestionRepository.AddAsync(selectedQuestion);
                }
                
                await _unitOfWork.CommitAsync();
            }
        }

        public async Task<int> PickProgressForUserAsync(int userId, int trackId)
        {
            var availableQuestions = (await _availableQuestionRepository
                   .FindAsync(aq => aq.UserId == userId && aq.TrackId == trackId)).Count();

            var answeredQuestions = (await _answeredQuestionRepository
                .FindAsync(aq => aq.UserId == userId && aq.TrackId == trackId)).Count();

            return availableQuestions - answeredQuestions;
        }

        public async Task<Question> PickQuestionForUserAsync(int userId, int trackId)
        {
            if (await HasUserFinishedTrackAsync(userId, trackId))
            {
                throw new Exception("O usuário atingiu o limite de questões da trilha.");
            }

            //TODO: Needs refactor to work properly with async
            return (await _availableQuestionRepository
                   .FindAsync(aq => aq.UserId == userId && aq.TrackId == trackId))
                   .Where(aq => !HasQuestionBeenAnsweredByUserAsync(userId, aq.QuestionId).GetAwaiter().GetResult())
                   .Select(aq => FindQuestionByIdAsync(aq.QuestionId).GetAwaiter().GetResult())
                   .FirstOrDefault();
        }

        public async Task SaveAnsweredQuestionAsync(AnsweredQuestion answeredQuestion)
        {
            if (await HasUserFinishedTrackAsync(answeredQuestion))
            {
                throw new Exception("O usuário já completou todas as suas questões da trilha.");
            }
            else if (await HasQuestionBeenAnsweredByUserAsync(answeredQuestion.UserId, answeredQuestion.QuestionId))
            {
                throw new Exception("O usuário já respondeu essa questão.");
            }
            else if (!await DoesAnswerBelongToQuestionAsync(answeredQuestion.AnswerId, answeredQuestion.QuestionId))
            {
                throw new Exception("A resposta não é uma das respostas possíveis para a questão.");
            }

            await _answeredQuestionRepository.AddAsync(answeredQuestion);
            await _unitOfWork.CommitAsync();
        }

        public async Task UpdateQuestionAsync(Question question)
        {
            if (await DoesQuestionHaveUserAnswersAsync(question.Id))
            {
                throw new Exception("A questão não pode ser alterada, pois usuários já responderam ela.");
            }
            else if (question.Answers.Count <= 1)
            {
                throw new Exception("A questão deve ter mais de uma alternativa possível.");
            }
            else if (question.Answers.Count(q => q.IsAnswerCorrect) != 1)
            {
                throw new Exception("A questão deve ter apenas uma resposta correta.");
            }

            foreach (var existingAnswer in await _answerRepository.FindAsync(a => a.QuestionId == question.Id))
            {
                _answerRepository.Remove(existingAnswer);
            }
            foreach (var newAnswer in question.Answers)
            {
                await _answerRepository.AddAsync(newAnswer);
            }
            _questionRepository.Update(question);
            await _unitOfWork.CommitAsync();
        }

        private async Task<bool> IsTrackAvailableAsync(int trackId) =>
            (await _trackRepository.FindAsync(x => x.Id == trackId && x.Available)).Any();

        private async Task<bool> DoesQuestionHaveUserAnswersAsync(int questionId) =>
            (await _answeredQuestionRepository.FindAsync(aq => aq.QuestionId == questionId)).Any();

        private async Task<bool> HaveUserQuestionsBeenCreatedAsync(int userId, int trackId) =>
            (await _availableQuestionRepository
                .FindAsync(aq => aq.TrackId == trackId && aq.UserId == userId)).Any();

        public async Task<IEnumerable<Question>> GetTrackQuestionsByLevelAsync(int trackId, QuestionLevel level) =>
            await _questionRepository.FindAsync(q => q.TrackId == trackId && q.QuestionLevel == level);

        private IEnumerable<AvailableQuestion> CreateTrackQuestionsForUser(int userId, int trackId, Random random) => 
            EnumHelper.GetEnumValues<QuestionLevel>()
                .Select(async level => await GetTrackQuestionsByLevelAsync(trackId, level))
                .Select(t => t.Result)
                .SelectMany(questions => questions
                                    .OrderBy(q => random.Next())
                                    .Take(AmountOfTrackQuestionsPerLevel))
                .OrderBy(q => random.Next())
                .Select(q => new AvailableQuestion()
                {
                    TrackId = trackId,
                    UserId = userId,
                    QuestionId = q.Id
                });
    }
}
